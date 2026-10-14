# Europa: New Origins (ALPHA)

Author: **Azdaj**  
Source release: **0.1.24**, for Vintage Story **1.22.7**.

Geographic world generation for Europe and the included part of North Africa. The code controls terrain, water, climate, vegetation, regional deposit maps, mountain snow and native story locations. The map adapts to world dimensions.

## Files

- `src/EuropeGen.cs`: server generation, story placement, commands, and the mountain snow block class.
- `src/biomes.json`: regional climate and vegetation settings.
- `src/resource-zones.json`: regional native deposit-map settings.
- `src/places.json`: geographic navigation and story target points.
- `assets/`: mountain snow block definition and translations.
- `SOURCE-MANIFEST.json`: SHA-256 hashes of the release ZIP, DLL, source and required data.

## Building

1. Install .NET SDK 10 and Vintage Story 1.22.7.
2. Download `Europa-New-Origins-0.1.24-source.zip` from this repository's Releases section and extract it. The complete archive contains all required binary map resources, which are intentionally omitted from Git history.
3. Open PowerShell in the extracted project folder.
4. If the game is installed elsewhere, set `$env:VINTAGE_STORY` to the folder containing `VintagestoryAPI.dll`.
5. Run `powershell -ExecutionPolicy Bypass -File .\zbuduj.ps1`.

The result is `paczki/europegengpt-test.zip`. The script builds and packages the mod; it does not install it or edit saved worlds. Original game DLLs are referenced from your installed game and are not redistributed here.

The source archive is **not an installable mod**. Players should download `Europa-New-Origins-0.1.24-alpha.zip` and put that ZIP in Mods without extracting it.

## Verification and limitations

The published release's DLL matches the locally compiled DLL used for testing. Compilation and focused code tests passed. Full seasonal snow behavior, quest progression and compatibility with other generators still require in-game testing. The release ZIP and extracted DLL were scanned with the local Microsoft Defender, which reported no threats; this is not a guarantee of safety.

`SOURCE-MANIFEST.json` identifies this snapshot and its data. A fresh build may differ byte-for-byte because of compiler versions, source paths and build metadata; do not assume identical hashes without checking the build environment.

## Data credits

Terrain data: GEBCO. Geographic data: Natural Earth.
The binary resources are processed map data used by the generator. This archive is a build snapshot, not the original GIS preprocessing dataset.

No code license has been selected yet. Dataset attribution and terms are separate from the code license.
