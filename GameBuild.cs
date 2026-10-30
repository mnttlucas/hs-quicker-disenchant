using System;

namespace HsQuickerDisenchant
{
	/// <summary>
	/// Reads the game build id (the ModuleVersionId of Assembly-CSharp.dll) so it can be logged and quoted in
	/// bug reports. Nothing is enforced with it. To validate: that the value is readable in the game's runtime.
	/// </summary>
	public static class GameBuild
	{
		public static string CurrentId { get; private set; }

		public static void Initialize()
		{
			try
			{
				CurrentId = typeof(CollectionManager).Module.ModuleVersionId.ToString();
			}
			catch (Exception exception)
			{
				CurrentId = "unknown";
				Plugin.Log.LogWarning("Could not read the game build id: " + exception.Message);
			}
		}
	}
}