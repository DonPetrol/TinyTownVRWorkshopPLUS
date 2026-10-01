# Workshop Plus 1.0

An upgrade for Tiny Town VR's Workshop tool (`Workshop.exe`). It keeps everything the original tool does and adds better model and texture importing, a much more capable item list, and many fixes. Everything it publishes uses the game's normal format, so other players see your items without installing anything.

## Install

1. Close `Workshop.exe` if it's open.
2. Open your game folder (in Steam: right-click Tiny Town VR > Manage > Browse local files), then go into `Workshop_Data\Managed`.
3. Make a backup. Find `Assembly-CSharp.dll`, copy it, and rename the copy to `Assembly-CSharp.dll.original` or something similar. Keep it in the same folder.
4. Copy both files from the package's folder, `Assembly-CSharp.dll` and `TTVRPlus.dll`, into `Workshop_Data\Managed`. When Windows asks, choose Replace the file in the destination.
5. Start `Workshop.exe`. The title at the top should read `Workshop PLUS`.

## Compared with the original tool

| | Original | Workshop Plus |
|---|---|---|
| Vertex limit | 65,000 face corners (about 6,500 to 16,000 Blender vertices) | 65,000 real vertices; shared vertices are merged |
| Model formats | OBJ only | OBJ, glTF and GLB |
| Materials | One texture, or colours only | Any mix of textures and colours, combined into one atlas |
| Textures | PNG, JPG (anything else shows a red/white **?**) | PNG, JPG, DDS (BC1-BC5, BC7, uncompressed) and TGA; size 512, 256 or 128px |
| Shading | Always flat | Imported, Smooth, Flat or Auto-Smooth |
| Item list | Unsorted, no search, one delete at a time | Thumbnails, search, sort, vertex/visibility columns, multi-select delete, bulk visibility, duplicate, Steam page links |
| Start-up and publishing | Window frozen while loading items and while publishing | Items load in parallel with progress; publishing runs in the background |
| Moved model files | The entry can't be opened any more | It asks where the file went and keeps all settings |
| Thumbnail | Automatic only | Automatic, from the camera, or your own image |
| Other | | Steam reconnect, Fix Subscriptions, Run Game, tooltips, scrollable help and description, decimal-comma PCs supported |

## Files

- Items and settings: `%USERPROFILE%\AppData\LocalLow\Lumbernauts\TinyTownWorkshop\Internal\Workshop\Development` (`workshopplus_*` files and folders).
- Log: `%USERPROFILE%\AppData\LocalLow\Lumbernauts\TinyTownWorkshop\output_log.txt` (lines tagged `[WorkshopPlus]`).
