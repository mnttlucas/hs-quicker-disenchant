using System;
using System.Collections.Generic;
using HarmonyLib;

namespace HsQuickerDisenchant
{
	/// <summary>A server answer to a craft request, as seen by the game.</summary>
	public sealed class SaleEvent
	{
		public Network.CardSaleResult.SaleResult Action;
		public string CardId;
		public TAG_PREMIUM Premium;
	}

	/// <summary>
	/// Remembers the server's answers while a run is in progress. It only stores data: it never calls the game
	/// and never triggers the next request (the runner does that on a later frame).
	/// </summary>
	public static class CardSaleCapture
	{
		private static readonly Queue<SaleEvent> Events = new Queue<SaleEvent>();
		private static readonly object Gate = new object();

		/// <summary>True once the Harmony patch below is installed.</summary>
		public static bool Installed;

		/// <summary>Answers are only kept while a run is in progress.</summary>
		public static bool Active;

		public static void Record(Network.CardSaleResult result)
		{
			if (!Active || result == null)
				return;
			Enqueue(new SaleEvent
			{
				Action = result.Action,
				CardId = result.AssetName,
				Premium = result.Premium
			});
		}

		public static void Enqueue(SaleEvent sale)
		{
			lock (Gate)
			{
				Events.Enqueue(sale);
			}
		}

		public static bool TryDequeue(out SaleEvent sale)
		{
			lock (Gate)
			{
				if (Events.Count == 0)
				{
					sale = null;
					return false;
				}
				sale = Events.Dequeue();
				return true;
			}
		}

		public static void Clear()
		{
			lock (Gate)
			{
				Events.Clear();
			}
		}
	}

	/// <summary>
	/// Reads the value returned by Network.GetCardSaleResult().
	/// It must NEVER call that method itself: it has a side effect (NetCache.AddExpectedCollectionModification)
	/// and is only meant to be called by CollectionManager.OnCardSale.
	/// </summary>
	[HarmonyPatch(typeof(Network), nameof(Network.GetCardSaleResult))]
	public static class GetCardSaleResultPatch
	{
		[HarmonyPostfix]
		public static void Postfix(Network.CardSaleResult __result)
		{
			try
			{
				CardSaleCapture.Record(__result);
			}
			catch (Exception exception)
			{
				Plugin.Log.LogError("Could not record the server's answer: " + exception);
			}
		}
	}
}