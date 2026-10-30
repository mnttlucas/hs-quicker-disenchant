using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace HsQuickerDisenchant
{
	[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
	public class Plugin : BaseUnityPlugin
	{
		public const string PluginGuid = "com.mnttlucas.hsquickerdisenchant";
		public const string PluginName = "HS Quicker Disenchant";
		public const string PluginVersion = "1.0.0";
		private const string RepositoryLink = "https://github.com/mnttlucas/hs-quicker-disenchant";

		// Firestone's Mod Manager looks for a config file named like the assembly.
		private const string ConfigFileName = "HsQuickerDisenchant.cfg";

		internal static ManualLogSource Log;

		private ConfigEntry<KeyCode> _hotkey;
		private ConfigEntry<CopyMode> _defaultCopyMode;
		private ConfigEntry<bool> _defaultCommon;
		private ConfigEntry<bool> _defaultRare;
		private ConfigEntry<bool> _defaultEpic;
		private ConfigEntry<bool> _defaultLegendary;
		private ConfigEntry<bool> _defaultNormal;
		private ConfigEntry<bool> _defaultGolden;
		private ConfigEntry<bool> _defaultSignature;
		private ConfigEntry<bool> _defaultDiamond;
		private ConfigEntry<bool> _enableRealSending;
		private ConfigEntry<float> _delayBetweenCards;
		private ConfigEntry<float> _responseTimeout;
		private ConfigEntry<int> _maxEntriesPerRun;
		private ConfigEntry<bool> _waitForCollectionUpdate;
		private ConfigEntry<bool> _blockGameMouse;
		private ConfigEntry<float> _backgroundOpacity;
		private ConfigEntry<int> _simulateFailureAtEntry;

		private HearthstoneAdapter _adapter;
		private HearthstoneSender _realSender;
		private AuditLog _audit;
		private DisenchantWindow _window;
		private BatchRunner _runner;

		private void Awake()
		{
			Log = Logger;

			ConfigFile config = new ConfigFile(Path.Combine(Paths.ConfigPath, ConfigFileName), true);

			// Required by Firestone's Mod Manager: Name, Version, Guid, DownloadLink (pointing to the repo).
			config.Bind("General", "Name", PluginName);
			config.Bind("General", "Guid", PluginGuid);
			config.Bind("General", "Version", PluginVersion);
			config.Bind("General", "DownloadLink", RepositoryLink);
			config.Bind("General", "Description",
				"Disenchant cards that rotated out of Standard, by expansion, rarity and version, with a full recap before confirming.");

			_hotkey = config.Bind("Features", "Hotkey", KeyCode.F8, "Opens / closes the window.");
			_defaultCopyMode = config.Bind("Defaults", "CopyMode", CopyMode.AllCopies,
				"AllCopies, or ExtraCopiesOnly (keep 2 copies of each card, 1 for legendaries).");
			_defaultCommon = config.Bind("Defaults", "Common", true, "Checked by default.");
			_defaultRare = config.Bind("Defaults", "Rare", true, "Checked by default.");
			_defaultEpic = config.Bind("Defaults", "Epic", true, "Checked by default.");
			_defaultLegendary = config.Bind("Defaults", "Legendary", false, "Checked by default.");
			_defaultNormal = config.Bind("Defaults", "Normal", true, "Checked by default.");
			_defaultGolden = config.Bind("Defaults", "Golden", false, "Checked by default.");
			_defaultSignature = config.Bind("Defaults", "Signature", false, "Checked by default.");
			_defaultDiamond = config.Bind("Defaults", "Diamond", false, "Checked by default.");

			_enableRealSending = config.Bind("Safety", "EnableRealSending", true,
				"true: cards are really disenchanted, after you review the recap and type the confirmation word. false: every run is only a simulation and nothing is sent.");
			_delayBetweenCards = config.Bind("Safety", "DelayBetweenCardsSeconds", 0f,
				"Optional pause between two requests. 0 = the next request is sent as soon as the server has answered the previous one.");
			_responseTimeout = config.Bind("Safety", "ResponseTimeoutSeconds", 15f,
				"The run stops if the server does not answer a request within this time.");
			_maxEntriesPerRun = config.Bind("Safety", "MaxEntriesPerRun", 0,
				"Maximum number of lines (a card in one version = one line) in one run. 0 = no limit.");
			_waitForCollectionUpdate = config.Bind("Safety", "WaitForCollectionUpdate", true,
				"After the server confirms a card, wait until your collection shows it before sending the next request (no fixed delay: it continues at once if already up to date). Each request carries the owned counts, so stale counts could be refused.");

			_blockGameMouse = config.Bind("Window", "BlockGameMouseOverWindow", true,
				"While the mouse is over this window, hide the mouse from the game so clicks on the window do not also click on the game behind it. Turn it off if it causes any problem with the game's mouse.");
			_backgroundOpacity = config.Bind("Window", "BackgroundOpacity", 0.6f,
				"How opaque the window background is, from 0 (the game's original translucent look) to 1 (fully opaque). The title bar keeps its original look.");

			_simulateFailureAtEntry = config.Bind("Debug", "SimulateFailureAtEntry", 0,
				"TESTING ONLY. In a simulated run, the server 'refuses' this entry (1 = first line, 0 = never) to test the stop path.");

			Log.LogInfo(_enableRealSending.Value ? "Real disenchanting is enabled." : "Simulation mode: nothing will be sent (EnableRealSending = false).");

			InstallPatches();
			GameBuild.Initialize();
			Log.LogInfo("Game build id: " + GameBuild.CurrentId);

			_adapter = new HearthstoneAdapter();
			_realSender = new HearthstoneSender();
			_audit = new AuditLog(Paths.BepInExRootPath);

			_window = new DisenchantWindow(_backgroundOpacity.Value);
			_window.OnReload = Load;
			_window.OnConfirm = StartRun;

			Log.LogInfo(PluginName + " " + PluginVersion + " loaded. Press " + _hotkey.Value + " to open.");
		}

		private void Update()
		{
			if (Input.GetKeyDown(_hotkey.Value))
				Toggle();
			if (_runner != null)
				_runner.Tick();
		}

		private void OnGUI()
		{
			if (_window != null)
				_window.Draw();
		}

		private void InstallPatches()
		{
			Harmony harmony = new Harmony(PluginGuid);

			// Each patch has its own try/catch so that one failing cannot disable the other.
			try
			{
				harmony.PatchAll();
				CardSaleCapture.Installed = true;
			}
			catch (Exception exception)
			{
				Log.LogError("Could not install the capture of the server's answers. Real sending is disabled: " + exception);
			}

			if (!_blockGameMouse.Value)
				return;

			try
			{
				WindowInputBlocker.Install(harmony);
				Log.LogInfo("The game's mouse is hidden while the pointer is over the window.");
			}
			catch (Exception exception)
			{
				Log.LogWarning("Could not block the game's mouse over the window: " + exception.Message);
			}
		}

		private void Toggle()
		{
			if (_window.Visible)
			{
				_window.Visible = false;
				return;
			}

			// A run keeps going while the window is closed: reopen it on the results instead of reloading.
			if (_runner != null && _runner.State == RunState.Running)
			{
				_window.ShowResults();
				_window.Visible = true;
				return;
			}

			Load();
			_window.Visible = true;
		}

		private void Load()
		{
			bool real = UseRealSender();
			_window.ConfirmLabel = real ? "DISENCHANT FOR REAL:" : "SIMULATE (nothing is sent):";
			_window.ConfirmNote = BuildConfirmNote(real);
			_window.SetData(_adapter.LoadCollection(), BuildDefaults());
		}

		private string BuildConfirmNote(bool real)
		{
			if (real)
				return "REAL RUN: each card is sent to the Hearthstone server one at a time, and the run stops at the first error. This cannot be undone.";
			return "SIMULATION: no request is sent. To send for real, set [Safety] EnableRealSending = true in the config and restart the game.";
		}

		private bool UseRealSender()
		{
			return _enableRealSending.Value && _realSender.IsAvailable;
		}

		/// <summary>Returns null when the run started, or the reason it was refused.</summary>
		private string StartRun(List<DisenchantItem> items)
		{
			ITransactionSender sender = UseRealSender()
				? (ITransactionSender)_realSender
				: new SimulatedSender(_simulateFailureAtEntry.Value);

			BatchRunner runner = new BatchRunner(_adapter, sender, _audit, _delayBetweenCards.Value, _responseTimeout.Value, _maxEntriesPerRun.Value, _waitForCollectionUpdate.Value);
			string refusal;
			if (!runner.Start(items, out refusal))
				return refusal;

			_runner = runner;
			_window.Runner = runner;
			return null;
		}

		private FilterOptions BuildDefaults()
		{
			return new FilterOptions
			{
				Mode = _defaultCopyMode.Value,
				Common = _defaultCommon.Value,
				Rare = _defaultRare.Value,
				Epic = _defaultEpic.Value,
				Legendary = _defaultLegendary.Value,
				Normal = _defaultNormal.Value,
				Golden = _defaultGolden.Value,
				Signature = _defaultSignature.Value,
				Diamond = _defaultDiamond.Value
			};
		}
	}
}