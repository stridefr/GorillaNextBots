using System;
using System.Collections.Generic;
using NextBots.Config;
using TMPro;
using UnityEngine;

namespace NextBots.UI
{
    /// <summary>
    /// The in-VR panel: a scrolling list you drive with discrete buttons, plus a config page.
    ///
    /// It floats *above* the hand and turns to face you, rather than being stuck to the wrist
    /// at the hand's own rotation. Two reasons: your hand goes straight through a panel
    /// mounted on it, and a wrist-mounted panel is only readable at one wrist angle.
    ///
    /// No pointer and no small targets - VR aiming at small things is miserable. Everything
    /// is chunky buttons, and every press writes a confirmation line so a button that worked
    /// but had nothing left to do is distinguishable from one that is broken.
    ///
    /// All geometry and colour comes from <see cref="PanelLayout"/> (panel.json), so it can be
    /// retuned from the browser design tool without a rebuild.
    /// </summary>
    public class NextBotMenu : MonoBehaviour
    {
        public enum Page { Spawn, Config }

        public IBotDirector Director;
        public SpawnAimer Aimer;
        public NextBotSettings Settings = NextBotSettings.Active;
        public PanelLayout Layout = new PanelLayout();

        public bool AnchorToLeftHand = true;
        public bool IsOpen { get; private set; }

        // ---- state ------------------------------------------------------------
        private Page _page = Page.Spawn;
        private int _spawnIndex;
        private int _configIndex;
        private int _scrollTop;

        /// <summary>
        /// Placement mode. SPAWN arms it and shows the ghost; the trigger on the aiming hand
        /// commits. Pressing a panel button to place is awkward because the panel is on the
        /// other hand from the one you are aiming with.
        /// </summary>
        private bool _armed;
        private bool _triggerWasDown;
        private bool _mouseDead;

        private string _confirm = "";
        private float _confirmUntil;
        private Color _confirmColor;

        private readonly List<PanelButton> _buttons = new List<PanelButton>(8);
        private readonly Dictionary<PanelButton, Material> _btnMat = new Dictionary<PanelButton, Material>();
        private readonly Dictionary<PanelButton, PanelText> _btnText = new Dictionary<PanelButton, PanelText>();
        private readonly Dictionary<PanelButton, float> _flashUntil = new Dictionary<PanelButton, float>();

        private readonly List<Material> _rowMats = new List<Material>(8);
        private readonly List<PanelText> _rowText = new List<PanelText>(8);
        private readonly List<PanelText> _rowValue = new List<PanelText>(8);

        private readonly HandTips _tips = new HandTips();
        private Transform _root;
        private PanelText _title, _subtitle, _footer, _status;
        private bool _built;
        private Vector3 _smoothPos;
        private Quaternion _smoothRot;
        private bool _posPrimed;

        private PanelButton _btnUp, _btnDown, _btnMinus, _btnPlus, _btnAction, _btnPage, _btnClear;

        // ======================================================================

        private void Update()
        {
            _tips.Update(Time.deltaTime);
            if (!IsOpen) return;

            UpdateAnchor();
            Refresh();
            TickButtons();
            TickPlacementTrigger();
        }

        public void Toggle() => SetOpen(!IsOpen);

        public void SetOpen(bool open)
        {
            if (open && !_built) Build();

            IsOpen = open;
            if (_root != null) _root.gameObject.SetActive(open);

            if (!open)
            {
                for (int i = 0; i < _buttons.Count; i++) _buttons[i].Release();
                _armed = false;
                if (Aimer != null) Aimer.SetActive(false);
                return;
            }

            _posPrimed = false;
            Say("READY", Layout.TextDim);
            Plugin.Log.LogInfo("[Panel] open | shader=" + (UiResources.Unlit != null ? UiResources.Unlit.name : "<NONE>") +
                               " | tmpFont=" + (UiResources.TmpFont != null ? UiResources.TmpFont.name : "<none>") +
                               " | tips=" + (_tips.Resolved ? "ok" : "NOT FOUND") +
                               " | size=" + Layout.width + "x" + Layout.height +
                               " | anchor=" + Layout.anchorMode);
        }

        /// <summary>Re-read panel.json and rebuild. Bound to a key for the design loop.</summary>
        public void ReloadLayout()
        {
            Layout = PanelLayout.Load();
            AnchorToLeftHand = Layout.anchorLeftHand;

            if (_root != null)
            {
                Destroy(_root.gameObject);
                _root = null;
            }
            _buttons.Clear(); _btnMat.Clear(); _btnText.Clear(); _flashUntil.Clear();
            _rowMats.Clear(); _rowText.Clear(); _rowValue.Clear();
            _built = false;

            if (IsOpen) { Build(); _root.gameObject.SetActive(true); }
            Say("LAYOUT RELOADED", Layout.Good);
            Plugin.Log.LogInfo("[Panel] layout reloaded.");
        }

        // ======================================================================
        //  Build
        // ======================================================================

        private void Build()
        {
            var L = Layout;
            var go = new GameObject("Panel");
            _root = go.transform;
            _root.SetParent(transform, false);

            var halfW = L.width * 0.5f;
            var halfH = L.height * 0.5f;

            Material m;
            UiResources.CreateQuad(_root, "bg", new Vector3(0, 0, 0.006f),
                new Vector2(L.width, L.height), L.Background, out m);

            // ---- header band
            var headerH = L.textTitle * 2.4f;
            UiResources.CreateQuad(_root, "header", new Vector3(0, halfH - headerH * 0.5f, 0.004f),
                new Vector2(L.width, headerH), L.Header, out m);

            _title = new PanelText(_root, "title",
                new Vector3(-L.width * 0.25f, halfH - headerH * 0.5f, 0f),
                L.width * 0.5f - 0.010f, L.textTitle, L.Text, TextAlignmentOptions.Left);
            _title.Text = "NEXTBOTS";

            _subtitle = new PanelText(_root, "subtitle",
                new Vector3(L.width * 0.25f, halfH - headerH * 0.5f, 0f),
                L.width * 0.5f - 0.010f, L.textTitle, L.Accent, TextAlignmentOptions.Right);

            // ---- list rows
            var listTop = halfH - headerH - L.rowGap;
            for (int i = 0; i < L.visibleRows; i++)
            {
                var y = listTop - L.rowHeight * 0.5f - i * (L.rowHeight + L.rowGap);

                Material rowMat;
                UiResources.CreateQuad(_root, "row" + i, new Vector3(0, y, 0.003f),
                    new Vector2(L.width - 0.016f, L.rowHeight), L.Row, out rowMat);
                _rowMats.Add(rowMat);

                var rowInner = L.width - 0.028f;
                var label = new PanelText(_root, "rowlbl" + i,
                    new Vector3(-rowInner * 0.175f, y, 0.001f),
                    rowInner * 0.65f, L.textRow, L.Text, TextAlignmentOptions.Left);
                _rowText.Add(label);

                var value = new PanelText(_root, "rowval" + i,
                    new Vector3(rowInner * 0.325f, y, 0.001f),
                    rowInner * 0.35f, L.textRow, L.Accent, TextAlignmentOptions.Right);
                _rowValue.Add(value);
            }

            var listBottom = listTop - L.visibleRows * (L.rowHeight + L.rowGap);

            // ---- buttons: two rows of controls under the list
            var bw = (L.width - 0.016f - L.buttonGap * 3f) / 4f;
            var by1 = listBottom - L.buttonHeight * 0.5f - L.buttonGap;
            var by2 = by1 - L.buttonHeight - L.buttonGap;

            float X(int col) => -halfW + 0.008f + bw * 0.5f + col * (bw + L.buttonGap);
            var bs = new Vector2(bw, L.buttonHeight);

            _btnUp     = Add("up",     "▲ UP",   new Vector2(X(0), by1), bs, true,  _ => Move(-1));
            _btnDown   = Add("down",   "▼ DOWN", new Vector2(X(1), by1), bs, true,  _ => Move(+1));
            _btnMinus  = Add("minus",  "–",      new Vector2(X(2), by1), bs, true,  _ => Adjust(-1));
            _btnPlus   = Add("plus",   "+",           new Vector2(X(3), by1), bs, true,  _ => Adjust(+1));

            _btnPage   = Add("page",   "CONFIG",      new Vector2(X(0), by2), bs, false, _ => TogglePage());
            _btnAction = Add("action", "SPAWN",       new Vector2(X(1), by2), bs, false, _ => DoAction());
            _btnClear  = Add("clear",  "CLEAR",       new Vector2(X(2), by2), bs, false, _ => DoClear());
            Add("undo", "UNDO", new Vector2(X(3), by2), bs, false, _ => DoUndo());

            // ---- footers
            var fy = -halfH + L.textFooter * 1.6f;
            _footer = new PanelText(_root, "footer", new Vector3(0, fy, 0f),
                L.width - 0.012f, L.textFooter, L.Text, TextAlignmentOptions.Center);

            _status = new PanelText(_root, "status", new Vector3(0, fy - L.textFooter * 1.6f, 0f),
                L.width - 0.012f, L.textFooter, L.Warn, TextAlignmentOptions.Center);

            _built = true;
            Refresh();
        }

        private PanelButton Add(string id, string label, Vector2 center, Vector2 size,
                                bool repeats, Action<PanelButton> onPress)
        {
            var btn = new PanelButton
            {
                Id = id,
                Label = label,
                Center = center,
                Size = size,
                Depth = Layout.buttonDepth,
                Repeats = repeats,
                OnPress = onPress
            };
            _buttons.Add(btn);

            Material mat;
            UiResources.CreateQuad(_root, "btn_" + id, new Vector3(center.x, center.y, 0.002f),
                size, Layout.Button, out mat);
            _btnMat[btn] = mat;

            var t = new PanelText(_root, "btnlbl_" + id, new Vector3(center.x, center.y, 0f),
                size.x, Layout.textButton, Layout.Text, TextAlignmentOptions.Center);
            t.Text = label;
            _btnText[btn] = t;

            return btn;
        }

        // ======================================================================
        //  Anchoring
        // ======================================================================

        /// <summary>
        /// Float above the hand and turn to face the player. Deliberately not parented to the
        /// hand's rotation: your hand passes straight through a panel mounted on it, and a
        /// wrist panel is only legible at one wrist angle.
        /// </summary>
        private void UpdateAnchor()
        {
            var L = Layout;
            var rig = GorillaTagger.Instance != null ? GorillaTagger.Instance.offlineVRRig : null;
            var hand = rig != null ? (AnchorToLeftHand ? rig.leftHandTransform : rig.rightHandTransform) : null;
            var viewer = UiResources.Viewer;

            // With the free cam on, the wrist is somewhere off screen (and with no headset it
            // is not tracked at all), so a hand-anchored panel is invisible. Park it in front
            // of whatever the person is actually looking through instead.
            if (UiResources.UsingFreeCam || !_tips.Resolved) hand = null;

            Vector3 targetPos;
            Quaternion targetRot;

            if (hand == null)
            {
                if (viewer == null) return;
                var fwd = viewer.forward;
                fwd.y *= 0.3f;
                fwd = fwd.sqrMagnitude > 0.0001f ? fwd.normalized : Vector3.forward;
                targetPos = viewer.position + fwd * 0.55f;
                targetRot = Quaternion.LookRotation(fwd, Vector3.up);
            }
            else
            {
                // World-up offset, so it sits above the hand no matter how the hand is turned.
                targetPos = hand.position + Vector3.up * L.anchorHeight;

                if (viewer != null && L.anchorForward != 0f)
                {
                    var toViewer = viewer.position - targetPos;
                    toViewer.y = 0f;
                    if (toViewer.sqrMagnitude > 0.0001f)
                        targetPos += toViewer.normalized * L.anchorForward;
                }

                if (L.faceHead && viewer != null)
                {
                    var away = targetPos - viewer.position;
                    if (away.sqrMagnitude < 0.0001f) away = Vector3.forward;
                    targetRot = Quaternion.LookRotation(away.normalized, Vector3.up) *
                                Quaternion.Euler(L.tiltDegrees, 0f, 0f);
                }
                else
                {
                    targetRot = hand.rotation * Quaternion.Euler(L.tiltDegrees, 180f, 0f);
                }
            }

            if (!_posPrimed || L.followSmoothing <= 0f)
            {
                _smoothPos = targetPos;
                _smoothRot = targetRot;
                _posPrimed = true;
            }
            else
            {
                var t = Mathf.Clamp01(Time.deltaTime * L.followSmoothing);
                _smoothPos = Vector3.Lerp(_smoothPos, targetPos, t);
                _smoothRot = Quaternion.Slerp(_smoothRot, targetRot, t);
            }

            transform.SetPositionAndRotation(_smoothPos, _smoothRot);
        }

        // ======================================================================
        //  Input
        // ======================================================================

        private void TickButtons()
        {
            // Only the free hand presses; the panel hand is right next to the buttons.
            var tip = _tips.For(!AnchorToLeftHand);
            if (!tip.Valid)
            {
                for (int i = 0; i < _buttons.Count; i++) _buttons[i].Release();
                return;
            }

            var now = Time.time;
            var localNow = _root.InverseTransformPoint(tip.Position);
            var localPrev = _root.InverseTransformPoint(tip.Previous);

            for (int i = 0; i < _buttons.Count; i++)
                if (_buttons[i].Tick(localPrev, localNow, now)) Flash(_buttons[i]);

            for (int i = 0; i < _buttons.Count; i++)
            {
                var b = _buttons[i];
                float until;
                var hot = _flashUntil.TryGetValue(b, out until) && now < until;
                Material mat;
                if (_btnMat.TryGetValue(b, out mat))
                    UiResources.TrySetColor(mat, !b.Enabled ? Layout.ButtonOff : hot ? Layout.ButtonHot : Layout.Button);
            }
        }

        private void Flash(PanelButton b)
        {
            _flashUntil[b] = Time.time + 0.09f;
            var tagger = GorillaTagger.Instance;
            if (tagger == null) return;
            try { tagger.StartVibration(!AnchorToLeftHand, tagger.tapHapticStrength * 0.5f, tagger.tapHapticDuration); }
            catch { /* haptics are a nicety */ }
        }

        // ======================================================================
        //  Actions
        // ======================================================================

        private int ItemCount =>
            _page == Page.Spawn
                ? (Director != null && Director.SpawnTypes != null ? Director.SpawnTypes.Count : 0)
                : Settings.Tunables.Count;

        private int Index
        {
            get { return _page == Page.Spawn ? _spawnIndex : _configIndex; }
            set { if (_page == Page.Spawn) _spawnIndex = value; else _configIndex = value; }
        }

        private void Move(int dir)
        {
            var n = ItemCount;
            if (n == 0) { Say("NOTHING HERE", Layout.Warn); return; }

            Index = Wrap(Index + dir, n);
            EnsureVisible();

            Say(_page == Page.Spawn
                    ? Director.SpawnTypes[Index]
                    : Settings.Tunables[Index].Label,
                Layout.Text);
        }

        private void EnsureVisible()
        {
            var rows = Mathf.Max(1, Layout.visibleRows);
            if (Index < _scrollTop) _scrollTop = Index;
            else if (Index >= _scrollTop + rows) _scrollTop = Index - rows + 1;
            _scrollTop = Mathf.Clamp(_scrollTop, 0, Mathf.Max(0, ItemCount - rows));
        }

        private void Adjust(int dir)
        {
            if (_page == Page.Spawn) { Move(dir); return; }

            if (Net.BotNetwork.SettingsLocked) { Say("HOST CONTROLS SETTINGS", Layout.Warn); return; }

            var t = Settings.Tunables;
            if (t.Count == 0) return;
            var r = t[Wrap(_configIndex, t.Count)].Adjust(dir, false);
            Say(r.Message, r.Changed ? Layout.Good : Layout.Warn);
        }

        private void TogglePage()
        {
            _page = _page == Page.Spawn ? Page.Config : Page.Spawn;
            _scrollTop = 0;
            EnsureVisible();
            Say(_page == Page.Spawn ? "SPAWN" : "CONFIG", Layout.TextDim);
        }

        private void DoAction()
        {
            if (_page == Page.Config) { Say("USE - / + TO ADJUST", Layout.TextDim); return; }

            if (Director == null) { Say("NOT READY", Layout.Warn); return; }
            if (!Director.CanSpawn) { Say(Director.BlockedReason, Layout.Warn); return; }

            _armed = !_armed;
            _triggerWasDown = true;   // ignore a trigger already held when arming
            Say(_armed ? "AIM + PULL TRIGGER" : "PLACEMENT OFF", Layout.Accent);
        }

        /// <summary>
        /// Commit a placement with the trigger on the aiming hand. Read here rather than in
        /// ModInput so it is scoped to placement mode and cannot fire while the panel is shut.
        /// </summary>
        private void TickPlacementTrigger()
        {
            if (!_armed) { _triggerWasDown = false; return; }

            var down = false;

            var poller = ControllerInputPoller.instance;
            if (poller != null)
            {
                // The aiming hand is whichever one is not wearing the panel.
                var aimRight = AnchorToLeftHand;
                var trigger = aimRight ? poller.rightControllerIndexFloat : poller.leftControllerIndexFloat;
                down = trigger > 0.6f;
            }

            // Desktop / free-cam: left mouse stands in for the trigger. Only while armed, so
            // it cannot fire during ordinary play.
            if (!down && !_mouseDead)
            {
                try { down = Input.GetMouseButton(0); }
                catch { _mouseDead = true; }
            }

            if (down && !_triggerWasDown) DoSpawn();
            _triggerWasDown = down;
        }

        private void DoSpawn()
        {
            if (Director == null) { Say("NOT READY", Layout.Warn); return; }
            if (!Director.CanSpawn) { Say(Director.BlockedReason, Layout.Warn); return; }

            Vector3 pos; Quaternion rot;
            if (Aimer == null || !Aimer.TryGetPlacement(out pos, out rot))
            {
                Say("AIM AT THE FLOOR", Layout.Warn);
                return;
            }

            string msg;
            var ok = Director.TrySpawn(_spawnIndex, pos, rot, out msg);
            Say(msg, ok ? Layout.Good : Layout.Warn);

            // Stay armed on success so several can be placed without going back to the panel.
            if (!ok) _armed = false;
        }

        private void DoUndo()
        {
            if (Director == null) return;
            string msg;
            var ok = Director.UndoLast(out msg);
            Say(msg, ok ? Layout.Good : Layout.Warn);
        }

        private void DoClear()
        {
            if (Director == null) return;
            var n = Director.ClearAll();
            Say(n > 0 ? "CLEARED " + n : "NOTHING TO CLEAR", n > 0 ? Layout.Good : Layout.Warn);
        }

        // ======================================================================
        //  Readout
        // ======================================================================

        private void Refresh()
        {
            if (!_built) return;
            var L = Layout;

            var canSpawn = Director != null && Director.CanSpawn;
            var n = ItemCount;
            if (n > 0) Index = Wrap(Index, n);
            EnsureVisible();

            _subtitle.Text = _page == Page.Spawn
                ? "SPAWN  " + (n == 0 ? "0/0" : (Index + 1) + "/" + n)
                : "CONFIG  " + (n == 0 ? "0/0" : (Index + 1) + "/" + n);

            _title.Text = "NEXTBOTS   " + (Director != null ? Director.LiveCount.ToString() : "0") + " LIVE";

            for (int i = 0; i < _rowText.Count; i++)
            {
                var idx = _scrollTop + i;
                var has = idx < n;
                var selected = has && idx == Index;

                UiResources.TrySetColor(_rowMats[i], selected ? L.RowSelected : L.Row);

                if (!has)
                {
                    _rowText[i].Text = "";
                    _rowValue[i].Text = "";
                    continue;
                }

                if (_page == Page.Spawn)
                {
                    _rowText[i].Text = Director.SpawnTypes[idx];
                    _rowValue[i].Text = "";
                }
                else
                {
                    var tun = Settings.Tunables[idx];
                    _rowText[i].Text = tun.Label;
                    _rowValue[i].Text = tun.Display;
                }

                _rowText[i].Color = selected ? L.Text : L.TextDim;
                _rowValue[i].Color = selected ? L.Text : L.Accent;
            }

            _btnPage.Label = _page == Page.Spawn ? "CONFIG" : "SPAWN";
            SetBtnText(_btnPage, _btnPage.Label);

            _btnAction.Enabled = _page == Page.Spawn && canSpawn;
            _btnClear.Enabled = canSpawn;
            SetBtnText(_btnAction, _page != Page.Spawn ? "--" : (_armed ? "CANCEL" : "SPAWN"));

            if (_page != Page.Spawn || !canSpawn) _armed = false;
            if (Aimer != null)
            {
                // The ghost shows the bot that SPAWN would place, so it follows the selection.
                Aimer.SkinIndex = _spawnIndex;
                Aimer.SetActive(_armed);
            }

            _status.Text = canSpawn ? "" : (Director != null ? Director.BlockedReason : "STARTING");
            _status.Color = L.Warn;

            if (Time.time > _confirmUntil) _footer.Text = "";
        }

        private void SetBtnText(PanelButton b, string text)
        {
            PanelText t;
            if (_btnText.TryGetValue(b, out t)) t.Text = text;
        }

        private void Say(string message, Color color)
        {
            _confirm = message ?? "";
            _confirmColor = color;
            _confirmUntil = Time.time + 2.5f;
            if (_footer != null) { _footer.Text = _confirm; _footer.Color = _confirmColor; }
        }

        private static int Wrap(int i, int count)
        {
            if (count <= 0) return 0;
            i %= count;
            return i < 0 ? i + count : i;
        }
    }
}
