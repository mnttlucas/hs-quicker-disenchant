using System;
using System.Collections.Generic;

namespace HsQuickerDisenchant
{
	public enum RunState
	{
		Idle,
		Running,
		Finished,
		Stopped
	}

	public enum ResultStatus
	{
		Sold,
		Failed,
		Skipped,
		Unconfirmed,
		NotAttempted
	}

	public sealed class RunResult
	{
		public DisenchantItem Item;
		public ResultStatus Status;
		public string Detail = string.Empty;
	}

	/// <summary>Sends one prepared request. The real implementation is intentionally not written yet.</summary>
	public interface ITransactionSender
	{
		/// <summary>True when nothing is really sent to the server.</summary>
		bool IsSimulation { get; }

		bool IsAvailable { get; }

		bool TrySend(PreparedRequest request, out string error);
	}

	/// <summary>Pretends to send. Answers through the same queue the real server answers use. No network at all.</summary>
	public sealed class SimulatedSender : ITransactionSender
	{
		private readonly int _failAtEntry;
		private int _sent;

		/// <param name="failAtEntry">1-based entry that gets a simulated server failure, 0 for never.</param>
		public SimulatedSender(int failAtEntry)
		{
			_failAtEntry = failAtEntry;
		}

		public bool IsSimulation
		{
			get
			{
				return true;
			}
		}

		public bool IsAvailable
		{
			get
			{
				return true;
			}
		}

		public bool TrySend(PreparedRequest request, out string error)
		{
			error = null;
			_sent++;
			CardSaleCapture.Enqueue(new SaleEvent
			{
				Action = _sent == _failAtEntry
					? Network.CardSaleResult.SaleResult.FAILED_WRONG_SELL_PRICE
					: Network.CardSaleResult.SaleResult.CARD_WAS_SOLD,
				CardId = request.Card.CardId,
				Premium = request.Premium
			});
			return true;
		}
	}

	/// <summary>
	/// Runs the disenchant requests one at a time. Driven by Tick() once per frame.
	/// Each request must be answered by the server before the next one is sent; the run stops at the first
	/// anomaly (failed check, failure answer, unexpected answer, no answer in time).
	/// </summary>
	public sealed class BatchRunner
	{
		private readonly HearthstoneAdapter _adapter;
		private readonly ITransactionSender _sender;
		private readonly AuditLog _audit;
		private readonly TimeSpan _delay;
		private readonly TimeSpan _timeout;
		private readonly int _maxEntries;
		private readonly bool _waitForCollectionUpdate;

		private readonly Queue<DisenchantItem> _queue = new Queue<DisenchantItem>();
		private readonly List<RunResult> _results = new List<RunResult>();

		private DisenchantItem _current;
		private PreparedRequest _currentRequest;
		private bool _waiting;
		private bool _stopRequested;
		private DateTime _sentAt;
		private DateTime _nextActionAt;
		private bool _awaitingCollectionUpdate;
		private DateTime _updateDeadline;
		private string _expectedCardId;
		private CardPremium _expectedPremium;
		private int _expectedOwned;

		public BatchRunner(HearthstoneAdapter adapter, ITransactionSender sender, AuditLog audit, float delaySeconds, float timeoutSeconds, int maxEntries, bool waitForCollectionUpdate)
		{
			_adapter = adapter;
			_sender = sender;
			_audit = audit;
			_delay = TimeSpan.FromSeconds(delaySeconds);
			_timeout = TimeSpan.FromSeconds(timeoutSeconds);
			_maxEntries = maxEntries;
			_waitForCollectionUpdate = waitForCollectionUpdate;
		}

		public RunState State { get; private set; }

		public string StopReason { get; private set; }

		/// <summary>True when the run stopped because of the server (refusal, unexpected or missing answer).</summary>
		public bool StoppedByServerError { get; private set; }

		public int Total { get; private set; }

		public List<RunResult> Results
		{
			get
			{
				return _results;
			}
		}

		public bool IsSimulation
		{
			get
			{
				return _sender.IsSimulation;
			}
		}

		public string AuditFilePath
		{
			get
			{
				return _audit.FilePath;
			}
		}

		public int Processed
		{
			get
			{
				return _results.Count;
			}
		}

		public string CurrentDescription
		{
			get
			{
				return _current == null ? string.Empty : _current.Describe();
			}
		}

		public bool Start(List<DisenchantItem> items, out string refusal)
		{
			refusal = null;
			if (State == RunState.Running)
			{
				refusal = "A run is already in progress.";
				return false;
			}
			if (items.Count == 0)
			{
				refusal = "Nothing to disenchant.";
				return false;
			}
			if (_maxEntries > 0 && items.Count > _maxEntries)
			{
				refusal = string.Format("Too many entries ({0}). The limit is {1} per run: untick some cards, or raise MaxEntriesPerRun in the config.", items.Count, _maxEntries);
				return false;
			}

			if (!_sender.IsSimulation)
			{
				if (!CardSaleCapture.Installed)
				{
					refusal = "The capture of the server's answers is not installed (see the BepInEx log). Nothing was sent.";
					return false;
				}
			}

			refusal = CheckGameState();
			if (refusal != null)
				return false;

			_queue.Clear();
			_results.Clear();
			foreach (DisenchantItem item in items)
			{
				_queue.Enqueue(item);
			}
			Total = items.Count;
			StopReason = string.Empty;
			StoppedByServerError = false;
			_awaitingCollectionUpdate = false;
			_current = null;
			_currentRequest = null;
			_waiting = false;
			_stopRequested = false;
			_nextActionAt = DateTime.UtcNow;

			CardSaleCapture.Clear();
			CardSaleCapture.Active = true;
			State = RunState.Running;

			DisenchantSummary summary = SelectionRules.Summarize(items);
			_audit.Write("RUN_START", (_sender.IsSimulation ? "SIMULATION" : "REAL") + " | " + items.Count + " entries, " + summary.CardCount + " cards, " + summary.Dust + " dust");
			return true;
		}

		public void RequestStop()
		{
			_stopRequested = true;
		}

		/// <summary>Call once per frame. Any unexpected error stops the run instead of leaving it stuck.</summary>
		public void Tick()
		{
			if (State != RunState.Running)
				return;

			try
			{
				TickInternal();
			}
			catch (Exception exception)
			{
				Plugin.Log.LogError("The run stopped because of an unexpected error: " + exception);
				if (_current != null && _waiting)
					Add(_current, ResultStatus.Unconfirmed, "Unexpected error while waiting for the server: " + exception.Message);
				else if (_current != null)
					Add(_current, ResultStatus.Skipped, "Unexpected error before the request was sent: " + exception.Message);
				End(RunState.Stopped, "unexpected error: " + exception.Message);
			}
		}

		private void TickInternal()
		{
			if (State != RunState.Running)
				return;

			DateTime now = DateTime.UtcNow;
			if (_waiting)
			{
				TickWaiting(now);
				return;
			}
			if (_stopRequested)
			{
				End(RunState.Stopped, "Stopped by the user.");
				return;
			}
			if (_awaitingCollectionUpdate)
			{
				TickCollectionUpdate(now);
				return;
			}
			if (now < _nextActionAt)
				return;
			TickSend(now);
		}

		private void TickWaiting(DateTime now)
		{
			SaleEvent sale;
			while (CardSaleCapture.TryDequeue(out sale))
			{
				if (sale.CardId != _currentRequest.Card.CardId || sale.Premium != _currentRequest.Premium)
				{
					Add(_current, ResultStatus.Unconfirmed, "Unexpected server answer for " + sale.CardId + " (" + sale.Premium + "): " + sale.Action);
					End(RunState.Stopped, "unexpected answer " + sale.Action + " for " + sale.CardId + ".", true);
					return;
				}
				if (sale.Action == Network.CardSaleResult.SaleResult.CARD_WAS_SOLD)
				{
					Add(_current, ResultStatus.Sold, string.Empty);
					_audit.Write("SOLD", _current.Describe());
					if (!_sender.IsSimulation && _waitForCollectionUpdate)
					{
						// The next request carries the owned counts, so they must be up to date before it is built.
						_expectedCardId = _currentRequest.Card.CardId;
						_expectedPremium = _currentRequest.Card.Premium;
						_expectedOwned = _currentRequest.OwnedThisVersion - _currentRequest.Quantity;
						_awaitingCollectionUpdate = true;
						_updateDeadline = now + _timeout;
					}
					_current = null;
					_waiting = false;
					_nextActionAt = now + _delay;
					return;
				}
				Add(_current, ResultStatus.Failed, "Server answered " + sale.Action);
				End(RunState.Stopped, "the server refused " + _current.Describe() + ": " + sale.Action + ".", true);
				return;
			}

			if (now - _sentAt > _timeout)
			{
				Add(_current, ResultStatus.Unconfirmed, "No answer within " + (int)_timeout.TotalSeconds + " s; the request may or may not have been processed.");
				End(RunState.Stopped, "no answer within " + (int)_timeout.TotalSeconds + " s for " + _current.Describe() + ". Check your collection before trying again.", true);
			}
		}

		private void TickCollectionUpdate(DateTime now)
		{
			if (_adapter.GetOwnedCount(_expectedCardId, _expectedPremium) <= _expectedOwned)
			{
				_awaitingCollectionUpdate = false;
				_nextActionAt = now + _delay;
				return;
			}
			if (now > _updateDeadline)
				End(RunState.Stopped, "the server confirmed the last card but your collection was not updated within " + (int)_timeout.TotalSeconds + " s. Nothing more was sent.", true);
		}

		private void TickSend(DateTime now)
		{
			if (_queue.Count == 0)
			{
				End(RunState.Finished, string.Empty);
				return;
			}

			string problem = CheckGameState();
			if (problem != null)
			{
				End(RunState.Stopped, problem);
				return;
			}

			DisenchantItem item = _queue.Dequeue();
			_current = item;
			PreparedRequest request;
			string error;
			if (!_adapter.PrepareRequest(item.Card, item.Quantity, out request, out error))
			{
				Add(item, ResultStatus.Skipped, error);
				End(RunState.Stopped, "Safety check failed for " + item.Describe() + ": " + error);
				return;
			}

			_audit.Write(_sender.IsSimulation ? "SIMULATED_SEND" : "SEND", item.Describe() + " | " + request.Describe());
			if (!_sender.TrySend(request, out error))
			{
				Add(item, ResultStatus.Failed, error);
				End(RunState.Stopped, "Could not send " + item.Describe() + ": " + error);
				return;
			}

			_current = item;
			_currentRequest = request;
			_sentAt = now;
			_waiting = true;
		}

		private string CheckGameState()
		{
			if (!_adapter.IsCollectionScreenActive())
				return "Open your collection screen first: the game only handles the server's answers there.";
			if (_adapter.HasPendingCraftingTransaction())
				return "The crafting screen has a transaction in progress. Close it first.";
			return null;
		}

		private void Add(DisenchantItem item, ResultStatus status, string detail)
		{
			_results.Add(new RunResult
			{
				Item = item,
				Status = status,
				Detail = detail
			});
			if (status != ResultStatus.Sold)
				_audit.Write(status.ToString().ToUpperInvariant(), item.Describe() + " | " + detail);
		}

		private void End(RunState state, string reason)
		{
			End(state, reason, false);
		}

		private void End(RunState state, string reason, bool serverError)
		{
			CardSaleCapture.Active = false;
			State = state;
			StopReason = reason;
			StoppedByServerError = serverError;
			_awaitingCollectionUpdate = false;
			_waiting = false;

			while (_queue.Count > 0)
			{
				_results.Add(new RunResult
				{
					Item = _queue.Dequeue(),
					Status = ResultStatus.NotAttempted,
					Detail = "The run stopped before this entry."
				});
			}
			_current = null;

			List<DisenchantItem> sold = new List<DisenchantItem>();
			List<RunResult> notDone = new List<RunResult>();
			int soldCards = 0;
			int soldDust = 0;
			foreach (RunResult result in _results)
			{
				if (result.Status == ResultStatus.Sold)
				{
					sold.Add(result.Item);
					soldCards += result.Item.Quantity;
					soldDust += result.Item.Dust;
				}
				else
				{
					notDone.Add(result);
				}
			}
			sold.Sort(SelectionRules.CompareByRarityThenName);
			notDone.Sort((a, b) => SelectionRules.CompareByRarityThenName(a.Item, b.Item));

			_audit.Write("RUN_END", (serverError ? "HS SERVER ERROR | " : string.Empty) + state + (string.IsNullOrEmpty(reason) ? string.Empty : " (" + reason + ")")
				+ " | disenchanted " + soldCards + " cards, " + soldDust + " dust | not done: " + notDone.Count + " entries");
			_audit.Write("LIST", "DISENCHANTED (by rarity, then name): " + sold.Count + " entries");
			foreach (DisenchantItem item in sold)
			{
				_audit.Write("  DONE", item.Card.Rarity + " | " + item.Describe());
			}
			_audit.Write("LIST", "NOT DISENCHANTED: " + notDone.Count + " entries");
			foreach (RunResult result in notDone)
			{
				_audit.Write("  NOT_DONE", result.Item.Card.Rarity + " | " + result.Item.Describe() + " | " + result.Status + " | " + result.Detail);
			}
		}
	}
}