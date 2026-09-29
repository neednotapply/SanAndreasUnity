using System.Collections.Generic;
using System.Linq;
using SanAndreasUnity.Importing.Items;
using SanAndreasUnity.Importing.Items.Placements;
using UGameCore.Utilities;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Builds San Andreas' water surface as a mesh asset.
    ///
    /// The state is an island - ocean on every side, plus rivers, canals and hundreds of pools - and without
    /// it the map ends at a cliff edge over nothing. water.dat describes the surface as faces of three or
    /// four vertices, each carrying its own height, so it is genuinely shaped rather than one flat plane:
    /// the sea, the Las Venturas canals and a backyard pool are all in there at their own levels.
    ///
    /// The faces are exported as a mesh asset rather than rebuilt at runtime, since Udon cannot construct
    /// meshes and the data never changes.
    ///
    /// Unity caps a single mesh at 65535 vertices with a 16-bit index buffer; the surface is split into
    /// several meshes instead of forcing 32-bit indices, which some platforms handle poorly.
    /// </summary>
    public static class WaterExporter
    {
        private const string OutputFolder = "Assets/ExportedAssets/Water";

        /// <summary> Kept well under the 16-bit limit so a face never straddles two meshes. </summary>
        private const int MaxVerticesPerMesh = 60000;

        public static void Export()
        {
            string path = Importing.Archive.ArchiveManager.PathToCaseSensitivePath(
                Config.GetPath("water_path"));

            var file = new WaterFile(path);

            // invisible faces exist to drive currents and drowning, not to be drawn
            var faces = file.Faces
                .Where(f => (f.Flags & WaterFlags.Visible) == WaterFlags.Visible)
                .ToArray();

            if (faces.Length == 0)
            {
                Debug.LogError("Water file contained no visible faces");
                return;
            }

            System.IO.Directory.CreateDirectory(OutputFolder);

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var indices = new List<int>();

            int meshIndex = 0;
            int written = 0;

            foreach (var face in faces)
            {
                // start a new mesh before this face could overflow the index buffer
                if (vertices.Count + face.Vertices.Length > MaxVerticesPerMesh)
                {
                    WriteMesh(meshIndex++, vertices, normals, indices);
                    written += vertices.Count;
                    vertices.Clear();
                    normals.Clear();
                    indices.Clear();
                }

                AppendFace(face, vertices, normals, indices);
            }

            if (vertices.Count > 0)
            {
                WriteMesh(meshIndex++, vertices, normals, indices);
                written += vertices.Count;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"Water exported: {faces.Length} visible faces, {written} vertices, " +
                $"{meshIndex} meshes -> {OutputFolder}");
        }

        /// <summary>
        /// Adds one water face.
        ///
        /// Faces are stored as a triangle strip of three or four vertices, so the winding alternates with
        /// each triangle - hence the flip. Getting that wrong leaves half the sea facing downwards and
        /// invisible from above.
        /// </summary>
        private static void AppendFace(
            WaterFace face, List<Vector3> vertices, List<Vector3> normals, List<int> indices)
        {
            int baseIndex = vertices.Count;

            for (int v = 0; v < face.Vertices.Length; v++)
            {
                vertices.Add(face.Vertices[v].Position);
                normals.Add(Vector3.up);
            }

            for (int i = 0; i < face.Vertices.Length - 2; i++)
            {
                int flip = i & 1;

                indices.Add(baseIndex + i + 1 - flip);
                indices.Add(baseIndex + i + 0 + flip);
                indices.Add(baseIndex + i + 2);
            }
        }

        private static void WriteMesh(
            int index, List<Vector3> vertices, List<Vector3> normals, List<int> indices)
        {
            var mesh = new Mesh();
            mesh.name = $"Water_{index}";
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetIndices(indices.ToArray(), MeshTopology.Triangles, 0);
            mesh.RecalculateBounds();

            string path = $"{OutputFolder}/Water_{index}.asset";

            if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null)
                AssetDatabase.DeleteAsset(path);

            AssetDatabase.CreateAsset(mesh, path);
        }
    }
}
