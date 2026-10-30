using System.Collections.Generic;

namespace HsQuickerDisenchant
{
	public enum CardRarity
	{
		Common = 0,
		Rare = 1,
		Epic = 2,
		Legendary = 3
	}

	public enum CardPremium
	{
		Normal = 0,
		Golden = 1,
		Signature = 2,
		Diamond = 3
	}

	public enum CopyMode
	{
		AllCopies,
		ExtraCopiesOnly
	}

	public enum CollectionStatus
	{
		NotReady,
		Ready
	}

	/// <summary>One owned card in one specific version. Only Wild cards that can be disenchanted end up here.</summary>
	public sealed class OwnedCard
	{
		public string CardId;
		public string Name;
		public string SetName;
		public CardRarity Rarity;
		public CardPremium Premium;

		/// <summary>Copies owned of this exact version.</summary>
		public int OwnedCount;

		/// <summary>Copies owned across all versions (normal + golden + signature + diamond).</summary>
		public int TotalCopiesAllVersions;

		/// <summary>Deck limit: 2, or 1 for legendaries.</summary>
		public int MaxCopies;

		/// <summary>Dust per copy.</summary>
		public int DisenchantValue;
	}

	/// <summary>One line of the recap: some copies of one version of a card.</summary>
	public sealed class DisenchantItem
	{
		public OwnedCard Card;
		public int Quantity;

		/// <summary>True when the user ticked this line off in the recap: these copies are kept.</summary>
		public bool Excluded;

		/// <summary>Identifies one card in one version (used to remember exclusions).</summary>
		public string Key
		{
			get
			{
				return Card.CardId + "|" + Card.Premium;
			}
		}

		/// <summary>Display format shared by the recap, the results screen and the audit log.</summary>
		public string Describe()
		{
			return string.Format("{0}x {1} [{2}]   {3} dust", Quantity, Card.Name, Card.Premium, Dust);
		}

		public int Dust
		{
			get
			{
				return Quantity * Card.DisenchantValue;
			}
		}
	}

	public sealed class SetTotal
	{
		public int Cards;
		public int Dust;
	}

	public sealed class DisenchantSummary
	{
		public int CardCount;
		public int Dust;
		public int[] ByRarity = new int[4];
		public int[] ByPremium = new int[4];
		public SortedDictionary<string, SetTotal> BySet = new SortedDictionary<string, SetTotal>();
	}

	/// <summary>Result of reading the collection, including why it may be empty.</summary>
	public sealed class CollectionSnapshot
	{
		public CollectionStatus Status = CollectionStatus.NotReady;

		/// <summary>Set when Status is NotReady: what the user should do.</summary>
		public string Problem = "";

		/// <summary>Wild cards that can be disenchanted.</summary>
		public List<OwnedCard> Cards = new List<OwnedCard>();

		/// <summary>Wild card entries found in the collection, disenchantable or not.</summary>
		public int WildEntries;

		/// <summary>Wild card entries that cannot be disenchanted (free cards, no dust value, not craftable).</summary>
		public int WildNotDisenchantable;
	}
}