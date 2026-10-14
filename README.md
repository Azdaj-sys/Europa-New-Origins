# Europa: New Origins (ALPHA)

Author: **Azdaj**  
Source release: **0.1.24**  
Game version: **Vintage Story 1.22.7**

Geographic world generation for Europe and the included part of North Africa. The mod controls terrain, water, climate, vegetation, regional mineral distribution, mountain snow, and native story locations.

The map automatically adapts to world width and length while preserving geographic proportions. Larger worlds create larger countries and longer travel distances rather than adding new countries. Mountain heights also adapt to world height.

## Files

- `src/EuropeGen.cs`: world generation, story placement, commands, and the mountain snow block class.
- `src/EuropeGen.csproj`: project configuration and required references.
- `src/biomes.json`: regional climate and vegetation settings.
- `src/resource-zones.json`: regional native deposit-map settings.
- `src/places.json`: geographic navigation and story target points.
- `src/modinfo.json`: mod name, author, version, and dependencies.
- `assets/`: mountain snow block definition and translations.
- `SOURCE-MANIFEST.json`: SHA-256 hashes identifying the source snapshot, map resources, and release files.

## Installation for players

1. Use Vintage Story 1.22.7.
2. Download `Europa-New-Origins-0.1.24-alpha.zip` from the mod's release page.
3. Close the game and place the ZIP in your `Mods` folder without extracting it.
4. Keep only one version of the mod installed.
5. Create a new world using standard world generation.
6. Enable lore content to include native story locations.

Install the mod on both client and server. All required map data is included in the playable mod ZIP.

The starting area and default respawn are near London, England.

## Suggested world settings

- Recommended world width and length: approximately **50,000 × 50,000 blocks**.
- Recommended world height: **448 or 512 blocks**.
- For quicker testing: approximately **25,000 × 25,000 blocks**.

## Building from source

The repository does not include an automated build script.

Building requires **.NET SDK 10**, **Vintage Story 1.22.7**, and all binary map resources listed in `src/EuropeGen.csproj`.

If a complete source archive is provided with the release, extract it to obtain the required map resources. Set the `VINTAGE_STORY` environment variable to your game installation folder, then run this command from the project root:

```powershell
dotnet build .\src\EuropeGen.csproj -c Release --configfile .\NuGet.Config -p:NuGetAudit=false
```

After compilation, create a ZIP containing:

- `EuropeGen.dll` and `modinfo.json` from `src/bin/Release/Mods/europegen/`, at the ZIP root.
- The complete `assets/` folder, also at the ZIP root.

Original game DLLs are referenced from your installed game and are not redistributed in this repository.

The source files and source archive are **not installable mods**.

## Verification and limitations

The release DLL matches the locally compiled DLL used for testing. Compilation and focused code tests passed.

The release ZIP and extracted DLL were scanned with the local Microsoft Defender, which reported no threats. This result is not a guarantee of safety.

Full seasonal snow behavior, quest progression, and compatibility with other mods still require further in-game testing. Do not combine this mod with another terrain replacement generator.

`SOURCE-MANIFEST.json` identifies the release snapshot and its required data. Fresh builds may differ byte-for-byte because of compiler versions, source paths, and build metadata.

## Development status

This is an alpha release. Further expansion, fixes, and improvements are planned.

When reporting an issue, include the game version, mod version, world dimensions, coordinates, screenshots, relevant logs, and other enabled mods.

## Data credits

Terrain data: **GEBCO**  
Geographic data: **Natural Earth**

Binary map resources are processed data used by the generator. They are not the original GIS preprocessing dataset.

No code license has been selected yet. Dataset attribution and terms are separate from the code license.
