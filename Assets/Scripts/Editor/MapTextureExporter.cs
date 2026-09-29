using System.IO;
using SanAndreasUnity.Importing.Conversion;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Builds San Andreas' map as a single texture asset.
    ///
    /// The game stores its map as 144 radar tiles - radar00 through radar143, each a 128px square - laid out
    /// in a twelve by twelve grid covering the whole state. Assembling them once at export time gives one
    /// 1536x1536 image that a minimap can sample with nothing more than a UV rectangle, which is well within
    /// what Udon can do; loading and stitching tiles at runtime is not.
    ///
    /// The grid maps directly onto the world: 6000 units across, centred on the origin, which is the same
    /// extent as the water surface.
    /// </summary>
    public static class MapTextureExporter
    {
        private const string OutputPath = "Assets/ExportedAssets/Map/SanAndreasMap.png";

        /// <summary> Tiles per side. </summary>
        private const int TileEdge = 12;

        /// <summary> Pixels per tile. </summary>
        private const int TileSize = 128;

        private const int MapSize = TileEdge * TileSize;

        public static void Export()
        {
            // Row-major is the layout the original importer assumes. It is verified by writing the
            // alternative alongside it the first time, since a wrong tile order produces a map that still
            // looks broadly like San Andreas and is easy to accept by mistake.
            Build(false, OutputPath);
        }

        /// <summary> Writes both candidate tile orders, for comparison. </summary>
        public static void ExportBothOrders()
        {
            Build(false, "Assets/ExportedAssets/Map/RowMajor.png");
            Build(true, "Assets/ExportedAssets/Map/ColumnMajor.png");
        }

        private static void Build(bool columnMajor, string outputPath)
        {
            var map = new Texture2D(MapSize, MapSize, TextureFormat.ARGB32, false, true);

            int loaded = 0;
            int missing = 0;

            for (int i = 0; i < TileEdge * TileEdge; i++)
            {
                string name = "radar" + (i < 10 ? "0" : string.Empty) + i;

                Texture2D tile = null;

                try
                {
                    var dictionary = TextureDictionary.Load(name);
                    var diffuse = dictionary.GetDiffuse(
                        name, new TextureLoadParams { makeNoLongerReadable = false });

                    if (diffuse != null)
                        tile = diffuse.Texture;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"Could not load map tile '{name}': {e.Message}");
                }

                if (null == tile)
                {
                    missing++;
                    continue;
                }

                // Tile dimensions are not assumed. A stride mismatch between the tile and the destination
                // rectangle shears every row of the map, which looks like a decoding fault but is really
                // just the wrong width.
                if (i < 4 || tile.width != TileSize || tile.height != TileSize)
                    Debug.Log($"  tile {name}: {tile.width}x{tile.height} format {tile.format}");

                // Tiles run left to right, top to bottom, but a Texture2D's origin is bottom left - hence
                // the vertical flip. Getting this wrong mirrors the state north to south, which is subtle
                // enough to miss and wrong enough to make the map useless.
                int column = columnMajor ? i / TileEdge : i % TileEdge;
                int rowFromTop = columnMajor ? i % TileEdge : i / TileEdge;

                int destinationX = column * TileSize;
                int destinationY = MapSize - (rowFromTop + 1) * TileSize;

                // Each tile is mirrored vertically as it is placed.
                //
                // The radar tiles are stored top-down while a Texture2D addresses rows bottom-up, so a
                // straight copy lays every tile in upside down. The map still looks broadly like San
                // Andreas that way - the tiles are in the right cells - but the content breaks at every
                // tile boundary, which reads as horizontal banding rather than as an obvious flip.
                Color[] source = tile.GetPixels();
                var flipped = new Color[source.Length];

                for (int rowIndex = 0; rowIndex < TileSize; rowIndex++)
                {
                    System.Array.Copy(
                        source,
                        rowIndex * TileSize,
                        flipped,
                        (TileSize - 1 - rowIndex) * TileSize,
                        TileSize);
                }

                map.SetPixels(destinationX, destinationY, TileSize, TileSize, flipped);

                loaded++;
            }

            map.Apply(false, false);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllBytes(outputPath, map.EncodeToPNG());

            Object.DestroyImmediate(map);

            AssetDatabase.Refresh();

            // the map is sampled by UV rectangle, so it must not wrap or be compressed into mush
            var importer = AssetImporter.GetAtPath(outputPath) as TextureImporter;
            if (importer != null)
            {
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = false;
                importer.maxTextureSize = 2048;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }

            Debug.Log($"Map texture exported: {loaded}/{TileEdge * TileEdge} tiles " +
                $"({missing} missing, {(columnMajor ? "column" : "row")}-major) -> {outputPath}");
        }
    }
}
