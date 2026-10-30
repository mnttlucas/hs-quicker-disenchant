using System;
using System.Collections.Generic;

namespace HsQuickerDisenchant
{
	/// <summary>
	/// The only class that talks to Hearthstone's internal types.
	///
	/// Verified from decompiled code:
	///  - CollectionManager.Get().GetOwnedCards() -> List&lt;CollectibleCard&gt; (one entry = one card in ONE version)
	///  - CollectibleCard: CardId, Name, Set, Rarity, PremiumType, OwnedCount, IsHeroSkin, IsCraftable,
	///    DefaultMaxCopiesPerDeck
	///  - GameUtils.GetCardSetFormat(set): 3 = Classic (VANILLA), 1 = rotated (Wild), 2 = Standard
	///  - CraftingManager.GetCardValue(cardId, premium) (static) -> NetCache.CardValue.GetSellValue()
	///
	/// Inferred from usage inside CollectibleCard (not read directly):
	///  - GameStrings.HasCardSetName(TAG_CARD_SET) / GameStrings.GetCardSetName(TAG_CARD_SET)
	///
	/// Assumption: GetCardSetFormat lives in GameUtils (it is called unqualified in the decompiled code).
	/// </summary>
	public sealed class HearthstoneAdapter
	{
		public CollectionSnapshot LoadCollection()
		{
			CollectionSnapshot snapshot = new CollectionSnapshot();

			List<CollectibleCard> owned;
			try
			{
				CollectionManager collection = CollectionManager.Get();
				owned = collection == null ? null : collection.GetOwnedCards();
			}
			catch (Exception exception)
			{
				Plugin.Log.LogError("LoadCollection: could not read the collection: " + exception);
				snapshot.Problem = "The collection could not be read. Open your collection in the game, then press Reload.";
				return snapshot;
			}

			if (owned == null || owned.Count == 0)
			{
				snapshot.Problem = "Your collection is not loaded yet. Open your collection in the game, then press Reload.";
				return snapshot;
			}

			snapshot.Status = CollectionStatus.Ready;

			// First pass: copies owned across all versions of each card (needed for "extra copies only").
			Dictionary<string, int> totals = new Dictionary<string, int>();
			foreach (CollectibleCard card in owned)
			{
				if (card.OwnedCount <= 0 || card.IsHeroSkin)
					continue;
				int total;
				totals.TryGetValue(card.CardId, out total);
				totals[card.CardId] = total + card.OwnedCount;
			}

			// Diagnostic counters: why entries are dropped.
			int errors = 0;
			int noCopy = 0;
			int heroSkins = 0;
			int notRotated = 0;
			int badRarity = 0;
			int notCraftable = 0;
			int noDust = 0;
			int[] byFormat = new int[8];
			Dictionary<string, int> setCounts = new Dictionary<string, int>();
			Dictionary<string, int> droppedRarity = new Dictionary<string, int>();
			List<TAG_CARD_SET> keptSets = new List<TAG_CARD_SET>();

			foreach (CollectibleCard card in owned)
			{
				try
				{
					// GetOwnedCards() also returns trial cards (OwnedCount + TrialCount >= 1).
					if (card.OwnedCount <= 0)
					{
						noCopy++;
						continue;
					}
					if (card.IsHeroSkin)
					{
						heroSkins++;
						continue;
					}

					TAG_CARD_SET set = card.Set;
					int format = (int)GameUtils.GetCardSetFormat(set);
					byFormat[format >= 0 && format < byFormat.Length ? format : 0]++;

					string setKey = set + " (fmt " + format + ")";
					int setCount;
					setCounts.TryGetValue(setKey, out setCount);
					setCounts[setKey] = setCount + 1;

					if (format != 1)
					{
						notRotated++;
						continue;
					}
					snapshot.WildEntries++;

					CardRarity rarity;
					if (!TryMapRarity(card.Rarity, out rarity))
					{
						badRarity++;
						string rarityKey = set + "/" + card.Rarity;
						int rarityCount;
						droppedRarity.TryGetValue(rarityKey, out rarityCount);
						droppedRarity[rarityKey] = rarityCount + 1;
						continue;
					}

					// Same guard as the game's own "disenchant extras" (CollectibleCard.DisenchantCount).
					if (!card.IsCraftable)
					{
						notCraftable++;
						continue;
					}

					int dust = GetDustValue(card.CardId, card.PremiumType);
					if (dust <= 0)
					{
						noDust++;
						continue;
					}

					snapshot.Cards.Add(new OwnedCard
					{
						CardId = card.CardId,
						Name = card.Name,
						Rarity = rarity,
						Premium = MapPremium(card.PremiumType),
						OwnedCount = card.OwnedCount,
						TotalCopiesAllVersions = totals[card.CardId],
						MaxCopies = card.DefaultMaxCopiesPerDeck,
						DisenchantValue = dust
					});
					keptSets.Add(set);
				}
				catch (Exception exception)
				{
					if (errors++ < 5)
						Plugin.Log.LogError("LoadCollection: skipped a card: " + exception);
				}
			}

			snapshot.WildNotDisenchantable = snapshot.WildEntries - snapshot.Cards.Count;

			Dictionary<TAG_CARD_SET, string> setLabels = BuildSetLabels(keptSets);
			for (int i = 0; i < snapshot.Cards.Count; i++)
				snapshot.Cards[i].SetName = setLabels[keptSets[i]];

			Plugin.Log.LogInfo("LoadCollection: GetOwnedCards=" + owned.Count
				+ " | trial-only=" + noCopy + " heroSkins=" + heroSkins
				+ " | format Wild=" + byFormat[1] + " Standard=" + byFormat[2] + " Classic=" + byFormat[3]
				+ " other=" + (byFormat[0] + byFormat[4] + byFormat[5] + byFormat[6] + byFormat[7])
				+ " | dropped in Wild: rarity=" + badRarity + " notCraftable=" + notCraftable + " noDust=" + noDust
				+ " | kept=" + snapshot.Cards.Count + " errors=" + errors);
			Plugin.Log.LogInfo("LoadCollection: owned sets -> " + JoinCounts(setCounts));
			Plugin.Log.LogInfo("LoadCollection: Wild cards dropped by rarity filter -> "
				+ (droppedRarity.Count == 0 ? "none" : JoinCounts(droppedRarity)));
			return snapshot;
		}

		/// <summary>
		/// True when a collection screen is active. The game only handles the server's answers to craft requests
		/// while one is (see RegisterCollectionNetHandlers). To validate: the exact meaning of GetCollectibleDisplay().
		/// </summary>
		public bool IsCollectionScreenActive()
		{
			CollectionManager collection = CollectionManager.Get();
			return collection != null && collection.GetCollectibleDisplay() != null;
		}

		/// <summary>
		/// True when the game's crafting screen has a transaction in progress.
		/// To validate: that GetPendingClientTransaction() has no side effect (e.g. creating an empty transaction).
		/// </summary>
		public bool HasPendingCraftingTransaction()
		{
			CraftingManager crafting = CraftingManager.Get();
			if (crafting == null)
				return false;
			CraftingPendingTransaction transaction = crafting.GetPendingClientTransaction();
			return transaction != null && transaction.HasPendingTransactions();
		}

		/// <summary>
		/// Builds, and checks, the transaction the game itself would build for this card (same steps as
		/// CraftingManager.TellServerAboutWhatUserDid). Sends nothing. Any anomaly returns false with the reason.
		/// </summary>
		public bool PrepareRequest(OwnedCard card, int quantity, out PreparedRequest request, out string problem)
		{
			request = null;
			if (quantity <= 0)
			{
				problem = "the quantity must be positive";
				return false;
			}

			TAG_PREMIUM premium = ToTagPremium(card.Premium);
			NetCache.CardValue value = CraftingManager.GetCardValue(card.CardId, premium);
			if (value == null)
			{
				problem = "the game has no value for this card";
				return false;
			}
			if (value.GetSellValue() != card.DisenchantValue)
			{
				problem = "the dust value changed since the collection was read (" + card.DisenchantValue + " then " + value.GetSellValue() + ")";
				return false;
			}

			// Re-read the owned counts right now, like the game does before every request.
			CollectionManager collection = CollectionManager.Get();
			int normal = collection.GetNumCopiesInCollection(card.CardId, TAG_PREMIUM.NORMAL);
			int golden = collection.GetNumCopiesInCollection(card.CardId, TAG_PREMIUM.GOLDEN);
			int signature = collection.GetNumCopiesInCollection(card.CardId, TAG_PREMIUM.SIGNATURE);
			int diamond = collection.GetNumCopiesInCollection(card.CardId, TAG_PREMIUM.DIAMOND);
			int ownedThisVersion = GetOwnedOfVersion(card.Premium, normal, golden, signature, diamond);
			if (ownedThisVersion < quantity)
			{
				problem = "only " + ownedThisVersion + " copies owned now, " + quantity + " planned";
				return false;
			}

			CraftingPendingTransaction transaction = new CraftingPendingTransaction
			{
				CardID = card.CardId,
				Premium = premium,
				CardValueOverridden = value.IsOverrideActive()
			};
			CraftingPendingTransaction.Operation operation = DisenchantOperation(card.Premium);
			for (int i = 0; i < quantity; i++)
			{
				transaction.Add(operation);
			}

			// The game's own cost is what gets sent; our estimate is only a cross-check (validated on 25 cards).
			int cost = transaction.GetExpectedTransactionCost(card.CardId);
			int estimate = -(quantity * card.DisenchantValue);
			if (cost != estimate)
			{
				problem = "the game's expected cost (" + cost + ") differs from ours (" + estimate + ")";
				return false;
			}

			request = new PreparedRequest
			{
				Card = card,
				Quantity = quantity,
				Premium = premium,
				Transaction = transaction,
				ExpectedCost = cost,
				OwnedThisVersion = ownedThisVersion,
				OwnedNormal = normal,
				OwnedGolden = golden,
				OwnedSignature = signature,
				OwnedDiamond = diamond
			};
			problem = null;
			return true;
		}

		/// <summary>Copies currently owned of one version, as the game's collection reports them.</summary>
		public int GetOwnedCount(string cardId, CardPremium premium)
		{
			return CollectionManager.Get().GetNumCopiesInCollection(cardId, ToTagPremium(premium));
		}

		private static int GetOwnedOfVersion(CardPremium premium, int normal, int golden, int signature, int diamond)
		{
			switch (premium)
			{
				case CardPremium.Golden:
					return golden;
				case CardPremium.Signature:
					return signature;
				case CardPremium.Diamond:
					return diamond;
				default:
					return normal;
			}
		}

		/// <summary>0 when the value is unknown (CardValue can be null): the card is then ignored.</summary>
		private static int GetDustValue(string cardId, TAG_PREMIUM premium)
		{
			NetCache.CardValue value = CraftingManager.GetCardValue(cardId, premium);
			return value == null ? 0 : value.GetSellValue();
		}

		/// <summary>
		/// Display name of each set: the game's localized name. The internal name is only added when two sets
		/// share the same display name, so that they stay distinguishable. Only the sets that are really listed
		/// are compared: a hidden set with the same name (for example Classic mode) must not change the label.
		/// </summary>
		private static Dictionary<TAG_CARD_SET, string> BuildSetLabels(List<TAG_CARD_SET> sets)
		{
			Dictionary<TAG_CARD_SET, string> names = new Dictionary<TAG_CARD_SET, string>();
			Dictionary<string, int> uses = new Dictionary<string, int>();
			foreach (TAG_CARD_SET set in sets)
			{
				if (names.ContainsKey(set))
				{
					continue;
				}
				string name = GetLocalizedSetName(set);
				names[set] = name;
				int count;
				uses.TryGetValue(name, out count);
				uses[name] = count + 1;
			}

			Dictionary<TAG_CARD_SET, string> labels = new Dictionary<TAG_CARD_SET, string>();
			foreach (KeyValuePair<TAG_CARD_SET, string> pair in names)
			{
				labels[pair.Key] = uses[pair.Value] > 1 ? pair.Value + " (" + pair.Key + ")" : pair.Value;
			}
			return labels;
		}

		private static string GetLocalizedSetName(TAG_CARD_SET set)
		{
			try
			{
				string name = GameStrings.HasCardSetName(set) ? GameStrings.GetCardSetName(set) : null;
				if (!string.IsNullOrEmpty(name))
				{
					return name;
				}
			}
			catch (Exception exception)
			{
				Plugin.Log.LogWarning("No localized name for set " + set + ": " + exception.Message);
			}
			return set.ToString();
		}

		private static string JoinCounts(Dictionary<string, int> counts)
		{
			List<string> keys = new List<string>(counts.Keys);
			keys.Sort(StringComparer.Ordinal);
			List<string> parts = new List<string>();
			foreach (string key in keys)
			{
				parts.Add(key + "=" + counts[key]);
			}
			return string.Join(", ", parts.ToArray());
		}

		private static CardPremium MapPremium(TAG_PREMIUM premium)
		{
			switch (premium)
			{
				case TAG_PREMIUM.GOLDEN:
					return CardPremium.Golden;
				case TAG_PREMIUM.SIGNATURE:
					return CardPremium.Signature;
				case TAG_PREMIUM.DIAMOND:
					return CardPremium.Diamond;
				default:
					return CardPremium.Normal;
			}
		}

		private static TAG_PREMIUM ToTagPremium(CardPremium premium)
		{
			switch (premium)
			{
				case CardPremium.Golden:
					return TAG_PREMIUM.GOLDEN;
				case CardPremium.Signature:
					return TAG_PREMIUM.SIGNATURE;
				case CardPremium.Diamond:
					return TAG_PREMIUM.DIAMOND;
				default:
					return TAG_PREMIUM.NORMAL;
			}
		}

		private static CraftingPendingTransaction.Operation DisenchantOperation(CardPremium premium)
		{
			switch (premium)
			{
				case CardPremium.Golden:
					return CraftingPendingTransaction.Operation.GoldenDisenchant;
				case CardPremium.Signature:
					return CraftingPendingTransaction.Operation.SignatureDisenchant;
				case CardPremium.Diamond:
					return CraftingPendingTransaction.Operation.DiamondDisenchant;
				default:
					return CraftingPendingTransaction.Operation.NormalDisenchant;
			}
		}

		private static bool TryMapRarity(TAG_RARITY source, out CardRarity rarity)
		{
			// Goes through the name so it does not depend on the enum's numeric values.
			switch (source.ToString())
			{
				case "COMMON":
					rarity = CardRarity.Common;
					return true;
				case "RARE":
					rarity = CardRarity.Rare;
					return true;
				case "EPIC":
					rarity = CardRarity.Epic;
					return true;
				case "LEGENDARY":
					rarity = CardRarity.Legendary;
					return true;
				default:
					rarity = CardRarity.Common;
					return false;
			}
		}
	}

	/// <summary>Everything the game needs for one craft request, already checked.</summary>
	public sealed class PreparedRequest
	{
		public OwnedCard Card;
		public int Quantity;
		public TAG_PREMIUM Premium;
		public CraftingPendingTransaction Transaction;
		public int ExpectedCost;
		public int OwnedThisVersion;
		public int OwnedNormal;
		public int OwnedGolden;
		public int OwnedSignature;
		public int OwnedDiamond;

		public string Describe()
		{
			return string.Format("expectedCost={0} (matches our estimate) | owned N/G/S/D={1}/{2}/{3}/{4} | override={5}",
				ExpectedCost, OwnedNormal, OwnedGolden, OwnedSignature, OwnedDiamond, Transaction.CardValueOverridden);
		}
	}

	/// <summary>
	/// The real sender: sends the same CraftingTransaction request the game's crafting screen sends, built and
	/// checked by HearthstoneAdapter.PrepareRequest. Only used when [Safety] EnableRealSending is true.
	/// </summary>
	public sealed class HearthstoneSender : ITransactionSender
	{
		public bool IsSimulation
		{
			get
			{
				return false;
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
			try
			{
				Network.Get().CraftingTransaction(request.Transaction, request.ExpectedCost,
					request.OwnedNormal, request.OwnedGolden, request.OwnedSignature, request.OwnedDiamond);
			}
			catch (Exception exception)
			{
				error = "the request could not be sent: " + exception.Message;
				return false;
			}
			error = null;
			return true;
		}
	}
}