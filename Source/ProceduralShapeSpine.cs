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
        public SpineNode Clone() => new SpineNode
        {
            position = position, sizeH = sizeH, sizeV = sizeV,
            profile = profile, mode = mode, slopeAbove = slopeAbove,
        };
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

        // --- Per-node editing proxies (PAW) ---
        // KSPFields are scalars, so these proxy whichever node `selectedNode` points at: changing one
        // writes back to that SpineNode. Non-persistent (the node list is the source of truth) except
        // selectedNode. Symmetry is handled manually (SyncToSymmetry), so they don't auto-propagate.
        [KSPField(isPersistant = true, guiActiveEditor = true, guiName = "Node", guiFormat = "F0", groupName = ProceduralPart.PAWGroupName),
            UI_FloatRange(minValue = 0, maxValue = 2, stepIncrement = 1, scene = UI_Scene.Editor, affectSymCounterparts = UI_Scene.None)]
        public float selectedNode = 0;

        [KSPField(isPersistant = false, guiActiveEditor = true, guiName = "Node mode", groupName = ProceduralPart.PAWGroupName),
            UI_ChooseOption(scene = UI_Scene.Editor, affectSymCounterparts = UI_Scene.None)]
        public string nodeModeOpt = "Proportional";

        [KSPField(isPersistant = false, guiActiveEditor = true, guiName = "Node size", guiFormat = "F3", guiUnits = "m", groupName = ProceduralPart.PAWGroupName),
            UI_FloatEdit(scene = UI_Scene.Editor, incrementSlide = SliderPrecision, sigFigs = 4, unit = "m", useSI = true, affectSymCounterparts = UI_Scene.None)]
        public float nodeSize = 1.25f;

        [KSPField(isPersistant = false, guiActiveEditor = true, guiName = "Node position", guiFormat = "F3", groupName = ProceduralPart.PAWGroupName),
            UI_FloatEdit(scene = UI_Scene.Editor, minValue = 0f, maxValue = 1f, incrementSlide = SliderPrecision, sigFigs = 3, affectSymCounterparts = UI_Scene.None)]
        public float nodePos = 0.5f;

        [KSPField(isPersistant = false, guiActiveEditor = true, guiName = "Slope above", groupName = ProceduralPart.PAWGroupName),
            UI_ChooseOption(scene = UI_Scene.Editor, affectSymCounterparts = UI_Scene.None)]
        public string nodeSlopeOpt = "Linear";

        [KSPEvent(guiActiveEditor = true, guiName = "Add node", groupName = ProceduralPart.PAWGroupName)]
        public void AddNodeEvent()
        {
            int i = SelIndex;
            SpineNode a = nodes[i];
            SpineNode b = nodes[Mathf.Min(i + 1, nodes.Count - 1)];
            float pos = (i < nodes.Count - 1) ? 0.5f * (a.position + b.position) : Mathf.Clamp01(a.position - 0.1f);
            var mid = new SpineNode
            {
                position = pos,
                sizeH = 0.5f * (a.sizeH + b.sizeH),
                sizeV = 0.5f * (a.sizeV + b.sizeV),
            };
            nodes.Add(mid);
            SortNodes();
            selectedNode = nodes.IndexOf(mid);
            RefreshSelector();
            LoadProxyFromNode();
            RebuildAndPropagate();
            MonoUtilities.RefreshPartContextWindow(part);
        }

        [KSPEvent(guiActiveEditor = true, guiName = "Remove node", groupName = ProceduralPart.PAWGroupName)]
        public void RemoveNodeEvent()
        {
            if (nodes.Count <= 2)
            {
                ScreenMessages.PostScreenMessage("Spine needs at least 2 nodes.", 4f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            nodes.RemoveAt(SelIndex);
            selectedNode = Mathf.Clamp((int)selectedNode, 0, nodes.Count - 1);
            RefreshSelector();
            LoadProxyFromNode();
            RebuildAndPropagate();
            MonoUtilities.RefreshPartContextWindow(part);
        }

        private int SelIndex => Mathf.Clamp((int)selectedNode, 0, Mathf.Max(0, nodes.Count - 1));

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

                Fields[nameof(selectedNode)].uiControlEditor.onFieldChanged = OnSelectedNodeChanged;
                UI_ChooseOption modeOpt = Fields[nameof(nodeModeOpt)].uiControlEditor as UI_ChooseOption;
                modeOpt.options = Enum.GetNames(typeof(SpineNodeMode));
                modeOpt.onFieldChanged = OnNodeFieldChanged;
                UI_ChooseOption slopeOpt = Fields[nameof(nodeSlopeOpt)].uiControlEditor as UI_ChooseOption;
                slopeOpt.options = Enum.GetNames(typeof(SpineSlope));
                slopeOpt.onFieldChanged = OnNodeFieldChanged;
                Fields[nameof(nodeSize)].uiControlEditor.onFieldChanged = OnNodeFieldChanged;
                Fields[nameof(nodePos)].uiControlEditor.onFieldChanged = OnNodeFieldChanged;

                RefreshSelector();
                LoadProxyFromNode();
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

            UI_FloatEdit sizeEdit = Fields[nameof(nodeSize)].uiControlEditor as UI_FloatEdit;
            sizeEdit.minValue = MinSize;
            sizeEdit.maxValue = MaxSize;
            sizeEdit.incrementLarge = PPart.diameterLargeStep;
            sizeEdit.incrementSmall = PPart.diameterSmallStep;

            ClampNodeSizes();   // heal any out-of-range sizes from older builds/crafts
            AdjustDimensionBounds();
            length = Mathf.Clamp(length, lengthEdit.minValue, lengthEdit.maxValue);
            hScale = Mathf.Clamp(hScale, hEdit.minValue, hEdit.maxValue);
            vScale = Mathf.Clamp(vScale, vEdit.minValue, vEdit.maxValue);
            LoadProxyFromNode();
        }

        #endregion

        #region Update

        // Set while a rebuild is in flight. The Volume setter fires onEditorShipModified, which can
        // synchronously rebuild the PAW and re-fire our proxy field handlers -> guard against that
        // re-entering and recursively mangling the node sizes (it was a stack overflow / crash).
        private bool _rebuilding;

        internal override void UpdateShape(bool force = true)
        {
            Profiler.BeginSample("UpdateShape Spine");
            bool prevRebuilding = _rebuilding;
            _rebuilding = true;
            try
            {
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

            GenerateSideMesh(rings);
            GenerateCapMesh(rings);
            GenerateColliderMesh(rings);

            UpdateNodeSize(BottomNodeName, EndDiameter(0));
            UpdateNodeSize(TopNodeName, EndDiameter(rings.Count - 1));

            // Set volume last (the setter fires OnPartVolumeChanged / onEditorShipModified) so the
            // visible mesh is already updated even if a volume listener throws.
            NominalVolume = CalculateVolume(rings);
            Volume = NominalVolume;

            PPart.UpdateProps();
            RaiseModelAndColliderChanged();
            }
            finally
            {
                _rebuilding = prevRebuilding;
                Profiler.EndSample();
            }
        }

        private struct Ring { public float y, rH, rV; }

        // Sane size bounds. A node "size" is a cross-section diameter. The hard 100 m cap on the
        // effective (scaled) radius keeps the volume well within the SI formatter's range -- an
        // absurd value made TankContentSwitcher throw "Illegal prefix".
        private float MaxSize => (PPart.diameterMax == float.PositiveInfinity) ? 50f : Mathf.Min(PPart.diameterMax, 50f);
        private float MinSize => Mathf.Max(0.1f, PPart.diameterMin);

        // Heal any out-of-range node sizes (e.g. a part bloated by an earlier bug) back into bounds.
        private void ClampNodeSizes()
        {
            foreach (SpineNode n in nodes)
            {
                n.sizeH = Mathf.Clamp(n.sizeH, MinSize, MaxSize);
                n.sizeV = Mathf.Clamp(n.sizeV, MinSize, MaxSize);
            }
        }

        private Ring NodeRing(SpineNode n) => new Ring
        {
            y = (n.position - 0.5f) * length,
            // Clamp to a finite positive radius so the volume can never go to 0/NaN/Infinity (which
            // the volume listeners reject) or so large the SI formatter overflows.
            rH = 0.5f * Mathf.Clamp(n.sizeH * hScale, 0.05f, 100f),
            rV = 0.5f * Mathf.Clamp(n.sizeV * vScale, 0.05f, 100f),
        };

        // Build the loft rings, subdividing each segment per its lower node's slopeAbove so the
        // silhouette curves (concave/convex/waisted). Linear segments need no extra rings.
        private List<Ring> BuildRings()
        {
            EnsureNodes();
            SortNodes();
            var rings = new List<Ring> { NodeRing(nodes[0]) };
            for (int i = 1; i < nodes.Count; i++)
            {
                Ring a = NodeRing(nodes[i - 1]);
                Ring b = NodeRing(nodes[i]);
                SpineSlope slope = nodes[i - 1].slopeAbove;
                int k = (slope == SpineSlope.Linear) ? 1 : 10;
                for (int s = 1; s <= k; s++)
                {
                    float t = (float)s / k;
                    float h = SlopeBlend(slope, t);
                    float w = WaistFactor(slope, t);
                    rings.Add(new Ring
                    {
                        y = Mathf.Lerp(a.y, b.y, t),
                        rH = Mathf.Lerp(a.rH, b.rH, h) * w,
                        rV = Mathf.Lerp(a.rV, b.rV, h) * w,
                    });
                }
            }
            return rings;
        }

        // Radius blend across a segment, t in [0,1]. Maps the straight 0..1 chord into a curve.
        private static float SlopeBlend(SpineSlope slope, float t)
        {
            switch (slope)
            {
                case SpineSlope.Concave: return t * t;            // caves inward (below the chord)
                case SpineSlope.Convex: return t * (2f - t);      // bulges outward (above the chord)
                default: return t;                                 // Linear / Waisted use the chord
            }
        }

        // Multiplicative pinch for the waisted profile (narrow in the middle), 1 otherwise.
        private static float WaistFactor(SpineSlope slope, float t) =>
            slope == SpineSlope.Waisted ? 1f - 0.35f * Mathf.Sin(Mathf.PI * t) : 1f;

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

        #region Node editing

        private void RefreshSelector()
        {
            if (Fields[nameof(selectedNode)].uiControlEditor is UI_FloatRange fr)
                fr.maxValue = Mathf.Max(0, nodes.Count - 1);
            selectedNode = SelIndex;
        }

        // Pull the selected node's data into the PAW proxy fields.
        private void LoadProxyFromNode()
        {
            if (nodes.Count == 0) return;
            SpineNode n = nodes[SelIndex];
            nodeModeOpt = n.mode.ToString();
            nodeSlopeOpt = n.slopeAbove.ToString();
            nodePos = n.position;
            nodeSize = SizeForMode(n);
        }

        private static float SizeForMode(SpineNode n)
        {
            switch (n.mode)
            {
                case SpineNodeMode.Vertical: return n.sizeV;
                case SpineNodeMode.Horizontal: return n.sizeH;
                default: return Mathf.Max(n.sizeH, n.sizeV);
            }
        }

        private void OnSelectedNodeChanged(BaseField f, object obj)
        {
            // Ignore re-fires triggered by our own rebuild, and no-op re-fires from PAW construction.
            if (_rebuilding) return;
            if (Equals(f.GetValue(this), obj)) return;
            LoadProxyFromNode();
            MonoUtilities.RefreshPartContextWindow(part);
        }

        // A proxy field (mode/size/position/slope) changed -> write back to the selected node.
        private void OnNodeFieldChanged(BaseField f, object obj)
        {
            // Ignore re-fires triggered by our own rebuild (the Volume setter can synchronously
            // rebuild the PAW), and the no-op re-fires PAW construction issues.
            if (_rebuilding) return;
            if (Equals(f.GetValue(this), obj)) return;
            if (nodes.Count == 0) return;
            SpineNode n = nodes[SelIndex];
            Debug.Log($"{ModTag} OnNodeFieldChanged {f.name}: {obj} -> {f.GetValue(this)} | sel={SelIndex}/{nodes.Count} before: {Dump(n)}");
            try
            {
                n.mode = ParseEnum(nodeModeOpt, SpineNodeMode.Proportional);
                n.slopeAbove = ParseEnum(nodeSlopeOpt, SpineSlope.Linear);

                // Position is clamped strictly between the neighbours so a node can't cross another.
                int idx = SelIndex;
                float loPos = (idx > 0) ? nodes[idx - 1].position + 0.01f : 0f;
                float hiPos = (idx < nodes.Count - 1) ? nodes[idx + 1].position - 0.01f : 1f;
                if (loPos > hiPos) loPos = hiPos = 0.5f * (loPos + hiPos);
                n.position = Mathf.Clamp(nodePos, loPos, hiPos);
                nodePos = n.position;   // reflect the clamp in the slider

                if (f.name == nameof(nodeSize))
                {
                    float size = Mathf.Clamp(nodeSize, MinSize, MaxSize);
                    switch (n.mode)
                    {
                        case SpineNodeMode.Vertical: n.sizeV = size; break;
                        case SpineNodeMode.Horizontal: n.sizeH = size; break;
                        default:
                            // Proportional: drive max(sizeH,sizeV) to `size`, preserving aspect ratio.
                            // Computed from the CURRENT sizes (not the event's old value) so a repeated
                            // fire is idempotent (ratio -> 1) and can never compound.
                            float curMax = Mathf.Max(n.sizeH, n.sizeV);
                            float ratio = (curMax > 0f) ? size / curMax : 1f;
                            n.sizeH *= ratio;
                            n.sizeV *= ratio;
                            break;
                    }
                    n.sizeH = Mathf.Clamp(n.sizeH, MinSize, MaxSize);
                    n.sizeV = Mathf.Clamp(n.sizeV, MinSize, MaxSize);
                }
                else if (f.name == nameof(nodeModeOpt))
                {
                    // Switching mode just changes what the Size slider means; refresh its display.
                    nodeSize = SizeForMode(n);
                    MonoUtilities.RefreshPartContextWindow(part);
                }

                // Position edits can reorder; keep the selection on the same node.
                SortNodes();
                selectedNode = Mathf.Clamp(nodes.IndexOf(n), 0, nodes.Count - 1);
                RebuildAndPropagate();
                Debug.Log($"{ModTag}  applied -> sel={SelIndex} after: {Dump(n)}");
            }
            catch (Exception e)
            {
                Debug.LogError($"{ModTag} OnNodeFieldChanged({f.name}) FAILED: {e}\n  nodes: {DumpAll()}");
            }
        }

        private static string Dump(SpineNode n) => $"[pos={n.position:F3} H={n.sizeH:F3} V={n.sizeV:F3} mode={n.mode} slope={n.slopeAbove}]";
        private string DumpAll() => string.Join(" ", nodes.ConvertAll(Dump).ToArray()) + $" | len={length:F3} hS={hScale:F3} vS={vScale:F3}";

        private static T ParseEnum<T>(string s, T fallback) where T : struct =>
            Enum.TryParse(s, out T v) ? v : fallback;

        private void RebuildAndPropagate()
        {
            UpdateShape();          // sets Volume -> fires OnPartVolumeChanged + onEditorShipModified
            UpdateInterops();       // FAR / TestFlight
            SyncToSymmetry();
        }

        // The node list isn't a scalar KSPField, so symmetry isn't auto-mirrored. Copy our state to
        // each counterpart's spine module and rebuild it.
        private void SyncToSymmetry()
        {
            foreach (Part p in part.symmetryCounterparts)
            {
                if (FindAbstractShapeModule(p, this) is ProceduralShapeSpine pm)
                {
                    pm.length = length;
                    pm.hScale = hScale;
                    pm.vScale = vScale;
                    pm.selectedNode = selectedNode;
                    pm.nodes.Clear();
                    foreach (SpineNode n in nodes) pm.nodes.Add(n.Clone());
                    pm.UpdateShape();
                    pm.UpdateInterops();
                    pm.LoadProxyFromNode();
                }
            }
        }

        #endregion

        #region Editor gizmo (visualization)

        // One handle sphere per node, on the node's top surface. Highlights the selected node so you
        // can see which node the PAW controls are editing. Visualization only for now -- dragging /
        // wheel / middle-click come in the interactive pass. Update() only runs while this shape is
        // the active (enabled) one; OnDisable cleans up when you switch shapes.
        private readonly List<GameObject> _handles = new List<GameObject>();

        public void Update()
        {
            if (!HighLogic.LoadedSceneIsEditor || nodes.Count == 0) { DestroyHandles(); return; }
            while (_handles.Count < nodes.Count) _handles.Add(CreateHandle());
            while (_handles.Count > nodes.Count)
            {
                GameObject extra = _handles[_handles.Count - 1];
                _handles.RemoveAt(_handles.Count - 1);
                if (extra != null) Destroy(extra);
            }
            int sel = SelIndex;
            for (int i = 0; i < nodes.Count; i++)
            {
                SpineNode n = nodes[i];
                float y = (n.position - 0.5f) * length;
                GameObject h = _handles[i];
                if (h == null) { _handles[i] = h = CreateHandle(); }
                h.transform.SetParent(part.transform, false);
                h.transform.localPosition = new Vector3(0f, y, 0f);   // on the spine axis
                h.transform.localRotation = Quaternion.identity;
                h.transform.localScale = Vector3.one * (i == sel ? 0.22f : 0.15f);
                h.layer = part.gameObject.layer;
                if (h.GetComponent<Renderer>() is Renderer r)
                    r.sharedMaterial = HandleMat(i == sel);   // ZTest Always -> visible through the hull
            }
        }

        private GameObject CreateHandle()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "SpineNodeHandle";
            if (go.GetComponent<Collider>() is Collider c) Destroy(c);
            return go;
        }

        private static Material _matSel, _matNorm;
        private static Material HandleMat(bool selected) =>
            selected ? (_matSel ??= MakeMat(Color.yellow)) : (_matNorm ??= MakeMat(new Color(0.2f, 0.8f, 1f)));

        // Unlit, solid, always-on-top material (renders through the mesh).
        private static Material MakeMat(Color col)
        {
            Material m = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            m.SetColor("_Color", col);
            m.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            m.SetInt("_ZWrite", 0);
            m.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            m.renderQueue = 5000;
            return m;
        }

        private void DestroyHandles()
        {
            foreach (GameObject h in _handles) if (h != null) Destroy(h);
            _handles.Clear();
        }

        public void OnDisable() => DestroyHandles();
        public void OnDestroy() => DestroyHandles();

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
