# HsQuickerDisenchant

A [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin for Hearthstone that lets you disenchant the cards of
expansions that rotated out of Standard in a few clicks, instead of one card at a time.

> **Version 1.0.0.** Real sending has been tested in real runs, up to a single run of 869 lines (1,210 cards) with no error. Disenchanting cannot be undone, so nothing is sent until you review the recap and type a confirmation word.

> ⚠️ **Disclaimer:** All client-side mods technically violate Blizzard's Terms of Service and carry a ban risk.
> Use at your own risk. This project is not affiliated with or endorsed by Blizzard Entertainment. Hearthstone is a trademark of Blizzard Entertainment.

## What it does

Press **F8** in game:

1. Pick the expansions you want to clean up. Only Wild cards that can actually be disenchanted are listed.
2. Choose which copies: all copies, or only the extra copies (keep 2 of each card, 1 for legendaries). Copies are counted across all versions of a card, and the cheapest versions are disenchanted first, so you keep your best ones.
3. Choose the rarities and the versions (normal, golden, signature, diamond). Defaults are configurable.
4. Review a complete recap: totals, by rarity, by version, by expansion, and the full card list.
5. Untick any card you want to keep (cards are listed by rarity, then name, with their version in brackets).
6. Confirm by typing a word. Cards are then disenchanted **one at a time**: the mod waits for the server's answer before sending the next request, and stops at the first error with a message and the list of the cards already disenchanted.
7. Read the results screen. Everything is also written to a log file in the `BepInEx` folder, named `HsQuickerDisenchant-audit-<date>.log` (one file per game session, created only when you run something; the 5 most recent are kept).

If you have no Wild card to disenchant, the window says so clearly.

## Safety

Disenchanting cannot be undone, so nothing is sent until you review the recap and type the confirmation word. Before starting a run, the mod checks that a collection screen is open and that the game's crafting screen has no transaction in progress. Before every request it re-reads your owned counts and the dust value, and compares the game's expected cost with its own estimate. It stops at the first anomaly. Use a test account for your first runs.

## Installation

You need BepInEx 5 in your Hearthstone folder, via **either** route, then the mod DLL.

**Option A — via Firestone:** with Hearthstone closed, open Firestone → Settings → General → Mods and enable mods.
Launch the game once and quit, so `<GameDir>\BepInEx\plugins\` exists.

**Option B — plain BepInEx:** extract BepInEx 5.4.x **x64** next to `Hearthstone.exe`, launch the game once and quit.

Then download `HsQuickerDisenchant.dll` from the [Releases](../../releases) page (or build it yourself, see below), drop it into `<GameDir>\BepInEx\plugins\` and launch the game.
Look for `HS Quicker Disenchant ... loaded.` in `<GameDir>\BepInEx\LogOutput.log`.
The mod writes the metadata (name, version, link) that Firestone's Mod Manager reads, but Firestone does not list or integrate it for now: install it manually as described above.

## Configuration

`<GameDir>\BepInEx\config\HsQuickerDisenchant.cfg` (created on first launch):

- `[Features] Hotkey` — key that opens the window (default `F8`).
- `[Defaults] CopyMode` — `AllCopies` or `ExtraCopiesOnly`.
- `[Defaults] Common / Rare / Epic / Legendary / Normal / Golden / Signature / Diamond` — what is checked by default.
- `[Safety] EnableRealSending` — `true` (default): cards are really disenchanted, after the recap and the confirmation. `false`: every run is a simulation and nothing is sent.
- `[Safety] DelayBetweenCardsSeconds` — optional pause between two requests (default `0`: the next request is sent as soon as the server has answered). Raise it if you get errors.
- `[Safety] WaitForCollectionUpdate` — after a confirmed card, wait until your collection shows it before the next request (default `true`; not a fixed delay).
- `[Safety] ResponseTimeoutSeconds` — the run stops if the server does not answer in time (default `15`).
- `[Safety] MaxEntriesPerRun` — maximum number of lines in one run (default `0` = no limit). A line is one card in one version, for example `2x Ysondre [Normal]`, and it is sent as one request.

- `[Window] BackgroundOpacity` — how opaque the window background is, from `0` (the game's original translucent look) to `1` (fully opaque). Default `0.6`. The title bar keeps its original look.
- `[Window] BlockGameMouseOverWindow` — default `true`. Stops clicks on the window from also reaching the game behind it. Turn it off if it causes any problem with the game's mouse.

## Known limitations

- The Diamond version has not been tested (almost no Diamond card can be disenchanted).
- The window uses the game's default Unity GUI look, translucent unless you raise `[Window] BackgroundOpacity`.

## Building from source

Requirements: a .NET SDK, and BepInEx 5 installed in the game folder (the project references its DLLs from there).

1. Edit `GameDir` in `HsQuickerDisenchant.csproj`.
2. `dotnet build -c Release` — the DLL is copied into `BepInEx\plugins` automatically.

No game files are included in this repository.

## Compatibility

Tested with the Hearthstone build that the BepInEx log reports as `Game build id: fca79a63-f7c9-4b6d-a204-d19f1c7a6d7f`.
Hearthstone patches can rename the methods this mod relies on. If it stops working after a game update, check
`LogOutput.log` and watch this repository for an updated release.

## License

[MIT](LICENSE)