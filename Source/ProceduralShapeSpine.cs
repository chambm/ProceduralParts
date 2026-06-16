using KSPAPIExtensions;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace ProceduralParts
{
    // Cross-section outline family. Phase 1 implements Ellipse/Circle; Mk2/Mk3 land in Phase 2.
    public enum SpineProfile { Ellipse, Circle, Mk2, Mk3 }

    // Which size a single (gizmo / Size-slider) adjustment changes for a node.
    public enum SpineNodeMode { Proportional, Vertical, Horizontal }

    // Silhouette of the segment ABOVE this node. Phase 1 implements Linear; the rest in Phase 2.
    public enum SpineSlope { Linear, Concave, Convex, Waisted }

    // One cross-section along the spine. Persisted as a SPINE_NODE child node (see OnSave/OnLoad),
    // the same variable-length pattern ProceduralSRB uses for its bell configs.
    [Serializable]
    public class SpineNode : IConfigNode
    {
        [Persistent] public float position = 0.5f;   // 0..1 along the spine (0 = bottom, 1 = top)
        [Persistent] public float sizeH = 1.25f;     // horizontal diameter (m), before hScale
        [Persistent] public float sizeV = 1.25f;     // vertical diameter (m), before vScale
        [Persistent] public SpineProfile profile = SpineProfile.Ellipse;
        [Persistent] public SpineNodeMode mode = SpineNodeMode.Proportional;
        [Persistent] public SpineSlope slopeAbove = SpineSlope.Linear;

        public SpineNode() { }
        public SpineNode(float pos, float h, float v) { position = pos; sizeH = h; sizeV = v; }
        public void Load(ConfigNode node) => ConfigNode.LoadObjectFromConfig(this, node);
        public void Save(ConfigNode node) => ConfigNode.CreateConfigFromObject(this, node);
    }

    class ProceduralShapeSpine : ProceduralAbstractShape
    {
        private const string ModTag = "[ProceduralShapeSpine]";
        private const int Sides = 24;          // ellipse tessellation for the visible mesh
        private const int ColliderSides = 12;  // coarser collider

        internal override void InitializeAttachmentNodes() => InitializeAttachmentNodes(length, EndDiameter(0));

        #region Config parameters

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Length", guiFormat = "F3", guiUnits = "m", groupName = ProceduralPart.PAWGroupName),
            UI_FloatEdit(scene = UI_Scene.Editor, incrementSlide = SliderPrecision, sigFigs = 5, unit = "m", useSI = true)]
        public float length = 2f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Width scale", guiFormat = "F3", groupName = ProceduralPart.PAWGroupName),
            UI_FloatEdit(scene = UI_Scene.Editor, incrementSlide = SliderPrecision, sigFigs = 3)]
        public float hScale = 1f;

        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Height scale", guiFormat = "F3", groupName = ProceduralPart.PAWGroupName),
            UI_FloatEdit(scene = UI_Scene.Editor, incrementSlide = SliderPrecision, sigFigs = 3)]
        public float vScale = 1f;

        [KSPField] public string TopNodeName = "top";
        [KSPField] public string BottomNodeName = "bottom";

        // Spine cross-sections, sorted by position. Serialized as SPINE_NODE subnodes.
        public readonly List<SpineNode> nodes = new List<SpineNode>();

        #endregion

        public override string ShapeKey
        {
            get
            {
                var sb = new StringBuilder("PP-Spine|").Append(length).Append('|').Append(hScale).Append('|').Append(vScale);
                foreach (SpineNode n in nodes)
                    sb.Append('|').Append(n.position).Append(',').Append(n.sizeH).Append(',').Append(n.sizeV)
                      .Append(',').Append((int)n.profile).Append(',').Append((int)n.slopeAbove);
                return sb.ToString();
            }
        }

        #region Serialization

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            ConfigNode[] cfgs = node.GetNodes("SPINE_NODE");
            if (cfgs != null && cfgs.Length > 0)
            {
                nodes.Clear();
                foreach (ConfigNode c in cfgs)
                {
                    SpineNode n = new SpineNode();
                    n.Load(c);
                    nodes.Add(n);
                }
                SortNodes();
            }
        }

        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            foreach (SpineNode n in nodes)
                n.Save(node.AddNode("SPINE_NODE"));
        }

        private void SortNodes() => nodes.Sort((a, b) => a.position.CompareTo(b.position));

        // Provide a sensible default spine if the part config / craft supplied none.
        private void EnsureNodes()
        {
            if (nodes.Count >= 2) return;
            nodes.Clear();
            nodes.Add(new SpineNode(0f, 1.25f, 1.25f));
            nodes.Add(new SpineNode(0.5f, 1.6f, 1.0f));
            nodes.Add(new SpineNode(1f, 0.8f, 0.8f));
        }

        #endregion

        #region Initialization

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            EnsureNodes();
            if (HighLogic.LoadedSceneIsEditor)
            {
                UpdateTechConstraints();
                Fields[nameof(length)].uiControlEditor.onFieldChanged = OnShapeDimensionChanged;
                Fields[nameof(hScale)].uiControlEditor.onFieldChanged = OnShapeDimensionChanged;
                Fields[nameof(vScale)].uiControlEditor.onFieldChanged = OnShapeDimensionChanged;
            }
        }

        public override void UpdateTechConstraints()
        {
            EnsureNodes();
            Fields[nameof(length)].guiActiveEditor = PPart.lengthMin != PPart.lengthMax;
            UI_FloatEdit lengthEdit = Fields[nameof(length)].uiControlEditor as UI_FloatEdit;
            lengthEdit.incrementLarge = PPart.lengthLargeStep;
            lengthEdit.incrementSmall = PPart.lengthSmallStep;

            UI_FloatEdit hEdit = Fields[nameof(hScale)].uiControlEditor as UI_FloatEdit;
            UI_FloatEdit vEdit = Fields[nameof(vScale)].uiControlEditor as UI_FloatEdit;
            hEdit.incrementLarge = vEdit.incrementLarge = 0.25f;
            hEdit.incrementSmall = vEdit.incrementSmall = 0.05f;

            AdjustDimensionBounds();
            length = Mathf.Clamp(length, lengthEdit.minValue, lengthEdit.maxValue);
            hScale = Mathf.Clamp(hScale, hEdit.minValue, hEdit.maxValue);
            vScale = Mathf.Clamp(vScale, vEdit.minValue, vEdit.maxValue);
        }

        #endregion

        #region Update

        internal override void UpdateShape(bool force = true)
        {
            Profiler.BeginSample("UpdateShape Spine");
            EnsureNodes();
            SortNodes();
            part.CoMOffset = CoMOffset;

            List<Ring> rings = BuildRings();
            float maxDia = 0f, minDia = float.MaxValue;
            foreach (Ring r in rings)
            {
                maxDia = Mathf.Max(maxDia, 2f * Mathf.Max(r.rH, r.rV));
                minDia = Mathf.Min(minDia, 2f * Mathf.Min(r.rH, r.rV));
            }
            MaxDiameter = maxDia;
            MinDiameter = minDia;
            InnerMaxDiameter = InnerMinDiameter = -1f;
            Length = length;
            NominalVolume = CalculateVolume(rings);
            Volume = NominalVolume;

            GenerateSideMesh(rings);
            GenerateCapMesh(rings);
            GenerateColliderMesh(rings);

            UpdateNodeSize(BottomNodeName, EndDiameter(0));
            UpdateNodeSize(TopNodeName, EndDiameter(rings.Count - 1));
            PPart.UpdateProps();
            RaiseModelAndColliderChanged();
            Profiler.EndSample();
        }

        private struct Ring { public float y, rH, rV; }

        private List<Ring> BuildRings()
        {
            var rings = new List<Ring>(nodes.Count);
            foreach (SpineNode n in nodes)
                rings.Add(new Ring
                {
                    y = (n.position - 0.5f) * length,
                    rH = 0.5f * n.sizeH * hScale,
                    rV = 0.5f * n.sizeV * vScale,
                });
            return rings;
        }

        // Bounding diameter of an end ring (used for stack node sizing).
        private float EndDiameter(int ringIndex)
        {
            EnsureNodes();
            SpineNode n = nodes[Mathf.Clamp(ringIndex, 0, nodes.Count - 1)];
            return Mathf.Max(n.sizeH * hScale, n.sizeV * vScale);
        }

        public override float CalculateVolume() => CalculateVolume(BuildRings());

        // Simpson integration of the elliptical section area along the spine (exact for the linear
        // radius taper of Phase 1; still a good approximation once slope curves arrive).
        private float CalculateVolume(List<Ring> rings)
        {
            float v = 0f;
            for (int i = 0; i < rings.Count - 1; i++)
            {
                Ring a = rings[i], b = rings[i + 1];
                float dy = b.y - a.y;
                float aArea = Mathf.PI * a.rH * a.rV;
                float bArea = Mathf.PI * b.rH * b.rV;
                float mArea = Mathf.PI * (0.5f * (a.rH + b.rH)) * (0.5f * (a.rV + b.rV));
                v += dy / 6f * (aArea + 4f * mArea + bArea);
            }
            return Mathf.Abs(v);
        }

        public override void AdjustDimensionBounds()
        {
            (Fields[nameof(length)].uiControlEditor as UI_FloatEdit).minValue = PPart.lengthMin;
            (Fields[nameof(length)].uiControlEditor as UI_FloatEdit).maxValue = PPart.lengthMax;

            // Scale ranges are derived from the part's diameter limits relative to the largest node.
            float largest = 0.01f;
            foreach (SpineNode n in nodes) largest = Mathf.Max(largest, Mathf.Max(n.sizeH, n.sizeV));
            float minScale = Mathf.Max(0.05f, PPart.diameterMin / largest);
            float maxScale = (PPart.diameterMax == float.PositiveInfinity) ? 10f : PPart.diameterMax / largest;
            foreach (string fn in new[] { nameof(hScale), nameof(vScale) })
            {
                (Fields[fn].uiControlEditor as UI_FloatEdit).minValue = minScale;
                (Fields[fn].uiControlEditor as UI_FloatEdit).maxValue = Mathf.Max(minScale + 0.1f, maxScale);
            }
        }

        public override bool SeekVolume(float targetVolume, int dir) => SeekVolume(targetVolume, Fields[nameof(length)], dir);

        public override void UpdateTFInterops()
        {
            ProceduralPart.tfInterface.InvokeMember("AddInteropValue", ProceduralPart.tfBindingFlags, null, null, new object[] { part, "diam1", MaxDiameter, "ProceduralParts" });
            ProceduralPart.tfInterface.InvokeMember("AddInteropValue", ProceduralPart.tfBindingFlags, null, null, new object[] { part, "diam2", MaxDiameter, "ProceduralParts" });
            ProceduralPart.tfInterface.InvokeMember("AddInteropValue", ProceduralPart.tfBindingFlags, null, null, new object[] { part, "length", length, "ProceduralParts" });
        }

        public override void TranslateAttachmentsAndNodes(BaseField f, object obj)
        {
            if (f.name == nameof(length) && obj is float oldLen)
                HandleLengthChange((float)f.GetValue(this), oldLen);
            else if ((f.name == nameof(hScale) || f.name == nameof(vScale)) && obj is float oldScale && oldScale > 0f)
                HandleDiameterChange((float)f.GetValue(this), oldScale);
        }

        // Phase-1 circular approximation: normalize by the widest radius. Phase 2 makes this
        // per-(angle, height) against the actual elliptical rings for accurate surface attach.
        public override void NormalizeCylindricCoordinates(ShapeCoordinates coords)
        {
            float r = Mathf.Max(0.01f, MaxDiameter / 2);
            coords.r /= r;
            coords.y /= Mathf.Max(0.01f, length);
        }

        public override void UnNormalizeCylindricCoordinates(ShapeCoordinates coords)
        {
            coords.r *= Mathf.Max(0.01f, MaxDiameter / 2);
            coords.y *= Mathf.Max(0.01f, length);
        }

        #endregion

        #region Meshes

        private static void WriteToAppropriateMesh(UncheckedMesh mesh, Mesh iconMesh, Mesh normalMesh)
        {
            if (HighLogic.LoadedScene == GameScenes.LOADING)
                mesh.WriteTo(iconMesh);
            else
                mesh.WriteTo(normalMesh);
        }

        private void GenerateSideMesh(List<Ring> rings)
        {
            int rc = rings.Count;
            int per = Sides + 1;                 // +1 wrap vertex for a clean UV seam
            var mesh = new UncheckedMesh(rc * per, (rc - 1) * Sides * 2);

            float span = rings[rc - 1].y - rings[0].y;
            if (span <= 0f) span = 1f;

            for (int r = 0; r < rc; r++)
            {
                float vCoord = (rings[r].y - rings[0].y) / span;
                Ring below = rings[Mathf.Max(0, r - 1)];
                Ring above = rings[Mathf.Min(rc - 1, r + 1)];
                Ring ring = rings[r];
                for (int j = 0; j <= Sides; j++)
                {
                    float ang = 2f * Mathf.PI * j / Sides;
                    float c = Mathf.Cos(ang), s = Mathf.Sin(ang);
                    int idx = r * per + j;
                    mesh.vertices[idx] = new Vector3(c * ring.rH, ring.y, s * ring.rV);

                    Vector3 pAbove = new Vector3(c * above.rH, above.y, s * above.rV);
                    Vector3 pBelow = new Vector3(c * below.rH, below.y, s * below.rV);
                    Vector3 tSpine = pAbove - pBelow;
                    Vector3 tRing = new Vector3(-s * ring.rH, 0f, c * ring.rV);
                    Vector3 normal = Vector3.Cross(tSpine, tRing).normalized;
                    Vector3 radial = new Vector3(c * ring.rV, 0f, s * ring.rH);
                    if (Vector3.Dot(normal, radial) < 0f) normal = -normal;
                    if (normal == Vector3.zero) normal = radial.normalized;
                    mesh.normals[idx] = normal;
                    Vector3 tan = tRing.normalized;
                    mesh.tangents[idx] = new Vector4(tan.x, tan.y, tan.z, 1f);
                    mesh.uv[idx] = new Vector2((float)j / Sides, vCoord);
                }
            }

            int t = 0;
            for (int r = 0; r < rc - 1; r++)
            {
                for (int j = 0; j < Sides; j++)
                {
                    int a = r * per + j;
                    int b = r * per + j + 1;
                    int cc = (r + 1) * per + j;
                    int d = (r + 1) * per + j + 1;
                    mesh.triangles[t++] = a; mesh.triangles[t++] = cc; mesh.triangles[t++] = b;
                    mesh.triangles[t++] = b; mesh.triangles[t++] = cc; mesh.triangles[t++] = d;
                }
            }

            float avgCirc = 0f;
            foreach (Ring r in rings) avgCirc += EllipsePerimeter(r.rH, r.rV);
            avgCirc /= rc;
            RaiseChangeTextureScale("sides", PPart.legacyTextureHandler.SidesMaterial, new Vector2(avgCirc, length));
            WriteToAppropriateMesh(mesh, PPart.SidesIconMesh, SidesMesh);
        }

        private void GenerateCapMesh(List<Ring> rings)
        {
            var mesh = new UncheckedMesh(Sides * 2, (Sides - 2) * 2);
            WriteCapVertices(mesh, rings[0], 0, false);
            WriteCapVertices(mesh, rings[rings.Count - 1], Sides, true);
            WriteCapTriangles(mesh, false, 0, 0);
            WriteCapTriangles(mesh, true, Sides, Sides - 2);
            WriteToAppropriateMesh(mesh, PPart.EndsIconMesh, EndsMesh);
        }

        private void WriteCapVertices(UncheckedMesh mesh, Ring ring, int offset, bool up)
        {
            float denom = Mathf.Max(0.01f, 2f * Mathf.Max(ring.rH, ring.rV));
            for (int j = 0; j < Sides; j++)
            {
                float ang = 2f * Mathf.PI * j / Sides;
                float c = Mathf.Cos(ang), s = Mathf.Sin(ang);
                int idx = offset + j;
                Vector3 pos = new Vector3(c * ring.rH, ring.y, s * ring.rV);
                mesh.vertices[idx] = pos;
                mesh.normals[idx] = new Vector3(0f, up ? 1f : -1f, 0f);
                mesh.tangents[idx] = new Vector4(1f, 0f, 0f, 1f);
                mesh.uv[idx] = new Vector2(pos.x / denom + 0.5f, pos.z / denom + 0.5f);
            }
        }

        private void WriteCapTriangles(UncheckedMesh mesh, bool up, int vertexOffset, int triOffset)
        {
            int tio = triOffset * 3;
            for (int i = 0; i < Sides - 2; i++)
            {
                mesh.triangles[i * 3 + tio] = vertexOffset;
                mesh.triangles[i * 3 + 1 + tio] = (up ? i + 2 : i + 1) + vertexOffset;
                mesh.triangles[i * 3 + 2 + tio] = (up ? i + 1 : i + 2) + vertexOffset;
            }
        }

        private void GenerateColliderMesh(List<Ring> rings)
        {
            int rc = rings.Count;
            int per = ColliderSides;
            int sideTris = (rc - 1) * ColliderSides * 2;
            int capTris = (ColliderSides - 2) * 2;
            var mesh = new UncheckedMesh(rc * per, sideTris + capTris);

            for (int r = 0; r < rc; r++)
            {
                Ring ring = rings[r];
                for (int j = 0; j < ColliderSides; j++)
                {
                    float ang = 2f * Mathf.PI * j / ColliderSides;
                    mesh.vertices[r * per + j] = new Vector3(Mathf.Cos(ang) * ring.rH, ring.y, Mathf.Sin(ang) * ring.rV);
                }
            }

            int t = 0;
            for (int r = 0; r < rc - 1; r++)
            {
                for (int j = 0; j < ColliderSides; j++)
                {
                    int a = r * per + j;
                    int b = r * per + (j + 1) % ColliderSides;
                    int c = (r + 1) * per + j;
                    int d = (r + 1) * per + (j + 1) % ColliderSides;
                    mesh.triangles[t++] = a; mesh.triangles[t++] = c; mesh.triangles[t++] = b;
                    mesh.triangles[t++] = b; mesh.triangles[t++] = c; mesh.triangles[t++] = d;
                }
            }
            int botOff = 0, topOff = (rc - 1) * per;
            for (int i = 0; i < ColliderSides - 2; i++)
            {
                mesh.triangles[t++] = botOff; mesh.triangles[t++] = botOff + i + 1; mesh.triangles[t++] = botOff + i + 2;
            }
            for (int i = 0; i < ColliderSides - 2; i++)
            {
                mesh.triangles[t++] = topOff; mesh.triangles[t++] = topOff + i + 2; mesh.triangles[t++] = topOff + i + 1;
            }

            var colliderMesh = new Mesh();
            mesh.WriteTo(colliderMesh);
            PPart.UpdateOrReplaceSingleCollider(colliderMesh);
        }

        private void UpdateNodeSize(string nodeName, float diameter)
        {
            AttachNode node = part.attachNodes.Find(n => n.id == nodeName);
            if (node == null) return;
            node.size = Math.Min((int)(diameter / PPart.diameterLargeStep), 3);
            node.breakingTorque = node.breakingForce = Mathf.Max(50 * node.size * node.size, 50);
            RaiseChangeAttachNodeSize(node, diameter, Mathf.PI * diameter * diameter * 0.25f);
            RaiseChangeTextureScale(nodeName, PPart.legacyTextureHandler.EndsMaterial, new Vector2(diameter, diameter));
        }

        private static float EllipsePerimeter(float a, float b)
        {
            // Ramanujan approximation.
            return Mathf.PI * (3f * (a + b) - Mathf.Sqrt((3f * a + b) * (a + 3f * b)));
        }

        #endregion
    }
}
