using System;
using System.Collections.Generic;

namespace HsQuickerDisenchant
{
	/// <summary>What the user chose: expansions, rarities, versions, and which copies.</summary>
	public sealed class FilterOptions
	{
		public HashSet<string> Sets = new HashSet<string>();
		public CopyMode Mode = CopyMode.AllCopies;

		/// <summary>Keys (see DisenchantItem.Key) of the lines the user chose to keep.</summary>
		public HashSet<string> ExcludedKeys = new HashSet<string>();

		public bool Common = true;
		public bool Rare = true;
		public bool Epic = true;
		public bool Legendary = false;

		public bool Normal = true;
		public bool Golden = false;
		public bool Signature = false;
		public bool Diamond = false;

		public bool AllowsRarity(CardRarity rarity)
		{
			switch (rarity)
			{
				case CardRarity.Common:
					return Common;
				case CardRarity.Rare:
					return Rare;
				case CardRarity.Epic:
					return Epic;
				default:
					return Legendary;
			}
		}

		public bool AllowsPremium(CardPremium premium)
		{
			switch (premium)
			{
				case CardPremium.Normal:
					return Normal;
				case CardPremium.Golden:
					return Golden;
				case CardPremium.Signature:
					return Signature;
				default:
					return Diamond;
			}
		}
	}

	/// <summary>Pure logic with no dependency on the game.</summary>
	public static class SelectionRules
	{
		public static List<DisenchantItem> Apply(IEnumerable<OwnedCard> cards, FilterOptions filters)
		{
			List<OwnedCard> source = new List<OwnedCard>(cards);
			Dictionary<OwnedCard, int> quantities = ComputeQuantities(source, filters.Mode);

			List<DisenchantItem> items = new List<DisenchantItem>();
			foreach (OwnedCard card in source)
			{
				int quantity = quantities[card];
				if (quantity <= 0)
					continue;
				if (!filters.Sets.Contains(card.SetName))
					continue;
				if (!filters.AllowsRarity(card.Rarity))
					continue;
				if (!filters.AllowsPremium(card.Premium))
					continue;
				DisenchantItem item = new DisenchantItem
				{
					Card = card,
					Quantity = quantity
				};
				item.Excluded = filters.ExcludedKeys.Contains(item.Key);
				items.Add(item);
			}

			items.Sort((a, b) =>
			{
				int bySet = string.CompareOrdinal(a.Card.SetName, b.Card.SetName);
				if (bySet != 0)
					return bySet;
				int byRarity = b.Card.Rarity.CompareTo(a.Card.Rarity);
				if (byRarity != 0)
					return byRarity;
				int byName = string.CompareOrdinal(a.Card.Name, b.Card.Name);
				if (byName != 0)
					return byName;
				return a.Card.Premium.CompareTo(b.Card.Premium);
			});
			return items;
		}

		/// <summary>Flat ordering used by the results screen and the audit log: rarity (highest first), then name, then version.</summary>
		public static int CompareByRarityThenName(DisenchantItem a, DisenchantItem b)
		{
			int byRarity = b.Card.Rarity.CompareTo(a.Card.Rarity);
			if (byRarity != 0)
				return byRarity;
			int byName = string.CompareOrdinal(a.Card.Name, b.Card.Name);
			if (byName != 0)
				return byName;
			return a.Card.Premium.CompareTo(b.Card.Premium);
		}

		/// <summary>
		/// AllCopies: every copy of every version.
		/// ExtraCopiesOnly: for each card, the copies beyond the deck limit (2, or 1 for legendaries), counted
		/// across all versions. The cheapest versions are disenchanted first, so the best versions are kept.
		/// </summary>
		private static Dictionary<OwnedCard, int> ComputeQuantities(List<OwnedCard> cards, CopyMode mode)
		{
			Dictionary<OwnedCard, int> result = new Dictionary<OwnedCard, int>();

			if (mode == CopyMode.AllCopies)
			{
				foreach (OwnedCard card in cards)
				{
					result[card] = card.OwnedCount;
				}
				return result;
			}

			Dictionary<string, List<OwnedCard>> byCardId = new Dictionary<string, List<OwnedCard>>();
			foreach (OwnedCard card in cards)
			{
				List<OwnedCard> versions;
				if (!byCardId.TryGetValue(card.CardId, out versions))
				{
					versions = new List<OwnedCard>();
					byCardId[card.CardId] = versions;
				}
				versions.Add(card);
			}

			foreach (KeyValuePair<string, List<OwnedCard>> pair in byCardId)
			{
				List<OwnedCard> versions = pair.Value;
				versions.Sort((a, b) => a.Premium.CompareTo(b.Premium));

				OwnedCard first = versions[0];
				int excess = Math.Max(first.TotalCopiesAllVersions - first.MaxCopies, 0);
				foreach (OwnedCard version in versions)
				{
					int take = Math.Min(version.OwnedCount, excess);
					result[version] = take;
					excess -= take;
				}
			}
			return result;
		}

		public static DisenchantSummary Summarize(IEnumerable<DisenchantItem> items)
		{
			DisenchantSummary summary = new DisenchantSummary();
			foreach (DisenchantItem item in items)
			{
				if (item.Excluded)
					continue;
				summary.CardCount += item.Quantity;
				summary.Dust += item.Dust;
				summary.ByRarity[(int)item.Card.Rarity] += item.Quantity;
				summary.ByPremium[(int)item.Card.Premium] += item.Quantity;

				SetTotal total;
				if (!summary.BySet.TryGetValue(item.Card.SetName, out total))
				{
					total = new SetTotal();
					summary.BySet[item.Card.SetName] = total;
				}
				total.Cards += item.Quantity;
				total.Dust += item.Dust;
			}
			return summary;
		}
	}
}