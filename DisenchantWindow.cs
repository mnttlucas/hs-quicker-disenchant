using System;
using System.Collections.Generic;
using UnityEngine;

namespace HsQuickerDisenchant
{
	/// <summary>
	/// IMGUI window with three screens: 1) choose expansions / copies / rarities / versions, 2) full recap,
	/// 3) progress and results of a run.
	/// </summary>
	public sealed class DisenchantWindow
	{
		private enum Screen
		{
			Filters,
			Recap,
			Results
		}

		private const int WindowId = 0x48534451;
		private const string ConfirmWord = "DISENCHANT";
		private const int MaxDisplayedResultLines = 300;

		// Height of the title strip of the default window style: the extra background layer starts below it.
		private const float TitleStripHeight = 20f;

		private readonly WindowBackground _background;
		private Rect _rect = new Rect(60, 60, 800, 640);
		private Vector2 _scroll;
		private Screen _screen = Screen.Filters;

		private CollectionSnapshot _snapshot = new CollectionSnapshot();
		private readonly SortedDictionary<string, SetTotal> _setInfo = new SortedDictionary<string, SetTotal>();
		private FilterOptions _filters = new FilterOptions();

		private List<DisenchantItem> _items = new List<DisenchantItem>();
		private DisenchantSummary _summary = new DisenchantSummary();
		private readonly SortedDictionary<string, List<DisenchantItem>> _itemsBySet = new SortedDictionary<string, List<DisenchantItem>>();
		private readonly HashSet<string> _expanded = new HashSet<string>();

		private string _typed = string.Empty;
		private string _refusal = string.Empty;
		private bool _dirty;

		// Results screen cache (rebuilt only when the runner's results change).
		private BatchRunner _listedRunner;
		private int _listedCount = -1;
		private readonly List<DisenchantItem> _sold = new List<DisenchantItem>();
		private readonly List<RunResult> _notDone = new List<RunResult>();
		private int _soldCards;
		private int _soldDust;

		private bool _visible;

		public bool Visible
		{
			get
			{
				return _visible;
			}
			set
			{
				_visible = value;
				WindowInputBlocker.SetVisible(value);
			}
		}
		public BatchRunner Runner;
		public Action OnReload;

		/// <summary>Returns null when the run started, or the reason it was refused.</summary>
		public Func<List<DisenchantItem>, string> OnConfirm;

		public string ConfirmLabel = "Disenchant";
		public string ConfirmNote = string.Empty;

		/// <param name="backgroundOpacity">0 keeps the game's translucent look, 1 is fully opaque.</param>
		public DisenchantWindow(float backgroundOpacity)
		{
			_background = new WindowBackground(backgroundOpacity);
		}

		public void SetData(CollectionSnapshot snapshot, FilterOptions defaults)
		{
			_snapshot = snapshot;
			_filters = defaults;
			_filters.Sets.Clear();
			_setInfo.Clear();
			_expanded.Clear();

			foreach (OwnedCard card in snapshot.Cards)
			{
				SetTotal total;
				if (!_setInfo.TryGetValue(card.SetName, out total))
				{
					total = new SetTotal();
					_setInfo[card.SetName] = total;
				}
				total.Cards += card.OwnedCount;
				total.Dust += card.OwnedCount * card.DisenchantValue;
			}

			_screen = Screen.Filters;
			_typed = string.Empty;
			_refusal = string.Empty;
			_scroll = Vector2.zero;
			Refresh();
		}

		public void Draw()
		{
			if (!Visible)
				return;
			_rect = GUI.Window(WindowId, _rect, DrawContents, "HS Quicker Disenchant");
			WindowInputBlocker.SetRect(_rect);
		}

		/// <summary>Brings the window back on the results screen (used when a run is still in progress).</summary>
		public void ShowResults()
		{
			_screen = Screen.Results;
			_scroll = Vector2.zero;
		}

		private void Refresh()
		{
			_items = SelectionRules.Apply(_snapshot.Cards, _filters);
			_summary = SelectionRules.Summarize(_items);

			_itemsBySet.Clear();
			foreach (DisenchantItem item in _items)
			{
				List<DisenchantItem> list;
				if (!_itemsBySet.TryGetValue(item.Card.SetName, out list))
				{
					list = new List<DisenchantItem>();
					_itemsBySet[item.Card.SetName] = list;
				}
				list.Add(item);
			}
			_dirty = false;
		}

		private void DrawContents(int id)
		{
			_background.Draw(new Rect(0, TitleStripHeight, _rect.width, _rect.height - TitleStripHeight));
			switch (_screen)
			{
				case Screen.Filters:
					DrawFilters();
					break;
				case Screen.Recap:
					DrawRecap();
					break;
				default:
					DrawResults();
					break;
			}
			GUI.DragWindow(new Rect(0, 0, 10000, 22));
		}

		// ---------- Screen 1: filters (and the empty states) ----------

		private void DrawFilters()
		{
			if (_snapshot.Status != CollectionStatus.Ready)
			{
				DrawMessage("Your collection is not ready", _snapshot.Problem);
				return;
			}

			if (_snapshot.Cards.Count == 0)
			{
				DrawNoWildCards();
				return;
			}

			GUILayout.Label("1. Choose the expansions to disenchant (only Wild cards that can be disenchanted are listed)");

			_scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(250));
			foreach (KeyValuePair<string, SetTotal> pair in _setInfo)
			{
				bool selected = _filters.Sets.Contains(pair.Key);
				string label = string.Format("{0}   ({1} cards owned, {2} dust if all copies)", pair.Key, pair.Value.Cards, pair.Value.Dust);
				bool now = Check(selected, label);
				if (now != selected)
				{
					if (now)
						_filters.Sets.Add(pair.Key);
					else
						_filters.Sets.Remove(pair.Key);
				}
			}
			GUILayout.EndScrollView();

			GUILayout.Label("2. Which copies");
			GUILayout.BeginHorizontal();
			bool allCopies = Check(_filters.Mode == CopyMode.AllCopies, "All copies");
			bool extraOnly = Check(_filters.Mode == CopyMode.ExtraCopiesOnly, "Extra copies only (keep 2 of each card, 1 for legendaries)");
			if (allCopies && _filters.Mode != CopyMode.AllCopies)
				_filters.Mode = CopyMode.AllCopies;
			else if (extraOnly && _filters.Mode != CopyMode.ExtraCopiesOnly)
				_filters.Mode = CopyMode.ExtraCopiesOnly;
			GUILayout.EndHorizontal();

			GUILayout.Label("3. Rarities");
			GUILayout.BeginHorizontal();
			_filters.Common = Check(_filters.Common, "Common");
			_filters.Rare = Check(_filters.Rare, "Rare");
			_filters.Epic = Check(_filters.Epic, "Epic");
			_filters.Legendary = Check(_filters.Legendary, "Legendary");
			GUILayout.EndHorizontal();

			GUILayout.Label("4. Versions");
			GUILayout.BeginHorizontal();
			_filters.Normal = Check(_filters.Normal, "Normal");
			_filters.Golden = Check(_filters.Golden, "Golden");
			_filters.Signature = Check(_filters.Signature, "Signature");
			_filters.Diamond = Check(_filters.Diamond, "Diamond");
			GUILayout.EndHorizontal();

			if (_dirty)
				Refresh();

			GUILayout.Space(8);
			if (_filters.Sets.Count == 0)
				GUILayout.Label("Select at least one expansion.");
			else if (_items.Count == 0)
				GUILayout.Label("No cards match your current choices.");
			else
				GUILayout.Label(string.Format("Currently matching: {0} cards  |  {1} dust", _summary.CardCount, _summary.Dust));

			GUILayout.BeginHorizontal();
			GUI.enabled = _items.Count > 0;
			if (GUILayout.Button("Review recap  >", GUILayout.Height(30)))
			{
				_screen = Screen.Recap;
				_typed = string.Empty;
				_refusal = string.Empty;
				_scroll = Vector2.zero;
			}
			GUI.enabled = true;
			DrawReloadAndClose();
			GUILayout.EndHorizontal();
		}

		private void DrawNoWildCards()
		{
			GUILayout.Space(20);
			GUILayout.Label("You have no Wild cards to disenchant.");
			GUILayout.Space(6);
			if (_snapshot.WildEntries > 0)
			{
				GUILayout.Label(string.Format(
					"Your collection has {0} Wild card entries, but none of them can be disenchanted (free or basic cards).",
					_snapshot.WildEntries));
			}
			else
			{
				GUILayout.Label("None of the cards you own belongs to an expansion that rotated out of Standard.");
			}
			GUILayout.Space(180);
			GUILayout.BeginHorizontal();
			DrawReloadAndClose();
			GUILayout.EndHorizontal();
		}

		private void DrawMessage(string title, string body)
		{
			GUILayout.Space(20);
			GUILayout.Label(title);
			GUILayout.Space(6);
			GUILayout.Label(body);
			GUILayout.Space(180);
			GUILayout.BeginHorizontal();
			DrawReloadAndClose();
			GUILayout.EndHorizontal();
		}

		private void DrawReloadAndClose()
		{
			GUI.enabled = OnReload != null;
			if (GUILayout.Button("Reload", GUILayout.Height(30), GUILayout.Width(120)))
				OnReload();
			GUI.enabled = true;
			if (GUILayout.Button("Close", GUILayout.Height(30), GUILayout.Width(120)))
				Visible = false;
		}

		// ---------- Screen 2: full recap ----------

		private void DrawRecap()
		{
			GUILayout.Label("RECAP - read carefully. This cannot be undone.");

			_scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(400));

			GUILayout.Label(string.Format("TOTAL: {0} cards  ->  {1} dust", _summary.CardCount, _summary.Dust));
			if (_summary.CardCount == 0)
				GUILayout.Label("Everything is excluded: there is nothing to disenchant.");
			GUILayout.Label("Copies: " + (_filters.Mode == CopyMode.AllCopies ? "all copies" : "extra copies only (keep 2 of each card, 1 for legendaries)"));
			GUILayout.Label(string.Format("By rarity:  Common {0}  |  Rare {1}  |  Epic {2}  |  Legendary {3}",
				_summary.ByRarity[0], _summary.ByRarity[1], _summary.ByRarity[2], _summary.ByRarity[3]));
			GUILayout.Label(string.Format("By version:  Normal {0}  |  Golden {1}  |  Signature {2}  |  Diamond {3}",
				_summary.ByPremium[0], _summary.ByPremium[1], _summary.ByPremium[2], _summary.ByPremium[3]));
			GUILayout.Label("Tick = disenchanted, untick = kept. Cards are sorted by rarity, then by name.");
			GUILayout.Space(6);

			foreach (KeyValuePair<string, List<DisenchantItem>> pair in _itemsBySet)
			{
				SetTotal total;
				if (!_summary.BySet.TryGetValue(pair.Key, out total))
					total = new SetTotal();
				bool open = _expanded.Contains(pair.Key);

				GUILayout.BeginHorizontal();
				if (GUILayout.Button(open ? "v" : ">", GUILayout.Width(26)))
				{
					if (open)
						_expanded.Remove(pair.Key);
					else
						_expanded.Add(pair.Key);
				}
				GUILayout.Label(string.Format("{0}: {1} cards, {2} dust", pair.Key, total.Cards, total.Dust));
				if (GUILayout.Button("All", GUILayout.Width(50)))
					SetExcluded(pair.Value, false);
				if (GUILayout.Button("None", GUILayout.Width(50)))
					SetExcluded(pair.Value, true);
				GUILayout.EndHorizontal();

				// IMGUI gets slow with thousands of rows, so only expanded sets draw their lines.
				if (!open)
					continue;

				bool hasRarity = false;
				CardRarity currentRarity = CardRarity.Common;
				foreach (DisenchantItem item in pair.Value)
				{
					if (!hasRarity || item.Card.Rarity != currentRarity)
					{
						currentRarity = item.Card.Rarity;
						hasRarity = true;
						GUILayout.Label("    " + currentRarity);
					}
					DrawItemLine(item);
				}
			}
			GUILayout.EndScrollView();

			GUILayout.Space(6);
			GUILayout.Label("These cards will be permanently disenchanted and will no longer be playable in Wild.");

			if (OnConfirm == null)
			{
				GUILayout.Label("(Execution is not available in this version.)");
			}
			else
			{
				if (!string.IsNullOrEmpty(ConfirmNote))
					GUILayout.Label(ConfirmNote);
				GUILayout.BeginHorizontal();
				GUILayout.Label("Type " + ConfirmWord + " to confirm:", GUILayout.Width(220));
				_typed = GUILayout.TextField(_typed, GUILayout.Width(200));
				GUILayout.EndHorizontal();
			}
			if (!string.IsNullOrEmpty(_refusal))
				GUILayout.Label("REFUSED: " + _refusal);

			GUILayout.BeginHorizontal();
			if (GUILayout.Button("<  Back", GUILayout.Height(30), GUILayout.Width(120)))
			{
				_screen = Screen.Filters;
				_refusal = string.Empty;
				_scroll = Vector2.zero;
			}
			GUI.enabled = OnConfirm != null && _typed == ConfirmWord && _summary.CardCount > 0;
			string confirmText = string.Format("{2} {0} cards ({1} dust)", _summary.CardCount, _summary.Dust, ConfirmLabel);
			if (GUILayout.Button(confirmText, GUILayout.Height(30)))
			{
				string refusal = OnConfirm(_items.FindAll(item => !item.Excluded));
				if (refusal == null)
				{
					_refusal = string.Empty;
					ShowResults();
				}
				else
				{
					_refusal = refusal;
				}
			}
			GUI.enabled = true;
			GUILayout.EndHorizontal();
		}

		/// <summary>One card line: [x] 2x Card Name [Golden]   200 dust</summary>
		private void DrawItemLine(DisenchantItem item)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Space(30);

			bool included = GUILayout.Toggle(!item.Excluded, string.Empty, GUILayout.Width(20));
			if (included == item.Excluded)
			{
				SetItemExcluded(item, !included);
				_summary = SelectionRules.Summarize(_items);
			}

			string line = item.Describe();
			if (item.Excluded)
				line += "   (kept)";
			GUILayout.Label(line);
			GUILayout.EndHorizontal();
		}

		private void SetItemExcluded(DisenchantItem item, bool excluded)
		{
			item.Excluded = excluded;
			if (excluded)
				_filters.ExcludedKeys.Add(item.Key);
			else
				_filters.ExcludedKeys.Remove(item.Key);
		}

		private void SetExcluded(List<DisenchantItem> items, bool excluded)
		{
			foreach (DisenchantItem item in items)
			{
				SetItemExcluded(item, excluded);
			}
			_summary = SelectionRules.Summarize(_items);
		}

		// ---------- Screen 3: progress and results ----------

		private void DrawResults()
		{
			BatchRunner runner = Runner;
			if (runner == null)
			{
				DrawMessage("No run", "There is no run to show.");
				return;
			}

			GUILayout.Label(runner.IsSimulation
				? "SIMULATION - nothing is sent to the server"
				: "REAL RUN - cards are being disenchanted");

			if (runner.State == RunState.Running)
				GUILayout.Label(string.Format("Running: {0}/{1} entries done. Current: {2}", runner.Processed, runner.Total, runner.CurrentDescription));
			else if (runner.State == RunState.Finished)
				GUILayout.Label("Finished.");
			else
				GUILayout.Label((runner.StoppedByServerError ? "HS server error: " : "STOPPED: ") + runner.StopReason);

			RefreshResultLists(runner);
			GUILayout.Label(string.Format("Disenchanted: {0} cards ({1} dust)  |  Not done: {2} entries", _soldCards, _soldDust, _notDone.Count));
			GUILayout.Label("Audit log: " + runner.AuditFilePath);
			if (runner.State == RunState.Stopped)
				GUILayout.Label("The cards disenchanted successfully before the stop are listed below.");

			_scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(380));

			GUILayout.Label("DISENCHANTED (by rarity, then name)");
			CardRarity currentRarity = CardRarity.Common;
			bool hasRarity = false;
			for (int i = 0; i < _sold.Count && i < MaxDisplayedResultLines; i++)
			{
				DisenchantItem item = _sold[i];
				if (!hasRarity || item.Card.Rarity != currentRarity)
				{
					currentRarity = item.Card.Rarity;
					hasRarity = true;
					GUILayout.Label("    " + currentRarity);
				}
				GUILayout.Label("        " + item.Describe());
			}
			if (_sold.Count > MaxDisplayedResultLines)
				GUILayout.Label(string.Format("... and {0} more entries (see the audit log)", _sold.Count - MaxDisplayedResultLines));

			if (_notDone.Count > 0)
			{
				GUILayout.Space(8);
				GUILayout.Label("NOT DISENCHANTED");
				for (int i = 0; i < _notDone.Count && i < MaxDisplayedResultLines; i++)
				{
					RunResult result = _notDone[i];
					string detail = string.IsNullOrEmpty(result.Detail) ? string.Empty : ": " + result.Detail;
					GUILayout.Label("    " + result.Item.Describe() + "   -> " + result.Status + detail);
				}
				if (_notDone.Count > MaxDisplayedResultLines)
					GUILayout.Label(string.Format("... and {0} more entries (see the audit log)", _notDone.Count - MaxDisplayedResultLines));
			}
			GUILayout.EndScrollView();

			GUILayout.BeginHorizontal();
			if (runner.State == RunState.Running)
			{
				if (GUILayout.Button("Stop after the current card", GUILayout.Height(30)))
					runner.RequestStop();
			}
			else
			{
				GUI.enabled = OnReload != null;
				if (GUILayout.Button("New run (reload collection)", GUILayout.Height(30)))
					OnReload();
				GUI.enabled = true;
				if (GUILayout.Button("Close", GUILayout.Height(30), GUILayout.Width(120)))
					Visible = false;
			}
			GUILayout.EndHorizontal();
		}

		private void RefreshResultLists(BatchRunner runner)
		{
			if (runner == _listedRunner && runner.Results.Count == _listedCount)
				return;
			_listedRunner = runner;
			_listedCount = runner.Results.Count;

			_sold.Clear();
			_notDone.Clear();
			_soldCards = 0;
			_soldDust = 0;
			foreach (RunResult result in runner.Results)
			{
				if (result.Status == ResultStatus.Sold)
				{
					_sold.Add(result.Item);
					_soldCards += result.Item.Quantity;
					_soldDust += result.Item.Dust;
				}
				else
				{
					_notDone.Add(result);
				}
			}
			_sold.Sort(SelectionRules.CompareByRarityThenName);
			_notDone.Sort((a, b) => SelectionRules.CompareByRarityThenName(a.Item, b.Item));
		}

		private bool Check(bool value, string label)
		{
			bool now = GUILayout.Toggle(value, label);
			if (now != value)
				_dirty = true;
			return now;
		}
	}
}