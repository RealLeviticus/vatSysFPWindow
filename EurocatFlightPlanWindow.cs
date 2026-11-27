using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using vatsys;
using States = vatsys.FDP2.FDR.FDRStates;

namespace EurocatFPWindow
{
    /// <summary>
    /// Flight Plan Window laid out like the Eurocat (TAAATS) FPL window, backed by the
    /// same vatSys FDP calls the stock window uses.
    /// </summary>
    internal class EurocatFlightPlanWindow : BaseForm
    {
        private enum Mode
        {
            View,
            Create,
            Modify,
            Depart
        }

        // ---- layout constants -------------------------------------------------
        private const int Pad = 8;
        private const int BtnW = 46;
        private const int BtnH = 32;
        private const int BtnGap = 2;
        private const int GroupGap = 12;
        private const int TimeColW = 96;

        private static readonly HashSet<string> WatchedProperties = new HashSet<string>
        {
            nameof(FDP2.FDR.State), nameof(FDP2.FDR.ControllerTracking), nameof(FDP2.FDR.ControllingSector),
            nameof(FDP2.FDR.AircraftCount), nameof(FDP2.FDR.AircraftType), nameof(FDP2.FDR.AircraftWake),
            nameof(FDP2.FDR.AircraftEquip), nameof(FDP2.FDR.AircraftSurvEquip), nameof(FDP2.FDR.DepAirport),
            nameof(FDP2.FDR.DesAirport), nameof(FDP2.FDR.AltAirport), nameof(FDP2.FDR.ETD), nameof(FDP2.FDR.ATD),
            nameof(FDP2.FDR.EET), nameof(FDP2.FDR.TAS), nameof(FDP2.FDR.RFL), nameof(FDP2.FDR.Route),
            nameof(FDP2.FDR.ParsedRoute), nameof(FDP2.FDR.Remarks), nameof(FDP2.FDR.FlightRules),
            nameof(FDP2.FDR.AssignedSSRCode), nameof(FDP2.FDR.LabelOpData), nameof(FDP2.FDR.CoupledTrack),
            nameof(FDP2.FDR.InhibitAutoCouple), nameof(FDP2.FDR.Callsign)
        };

        private readonly Font font = MMI.eurofont_winverysml;

        // ---- state ------------------------------------------------------------
        private FDP2.FDR _fdr;
        private Mode _mode = Mode.View;
        private bool _updating;
        private bool _requestPending;
        private Timer _requestTimer;
        private Action _unsubscribeNoFp;
        private Track _routeTrack;

        // ---- function buttons ---------------------------------------------------
        private EcButton nextButton, prevButton;
        private EcButton creButton, modButton, depButton;
        private EcButton crteButton, rplButton, estButton, holdButton, steButton, backButton, prlButton, tmaButton, cflButton;
        private EcButton accButton, cnlButton, cbepButton, hndButton, inhiButton, exitButton, hndiButton, supButton;
        private EcButton ncouButton, bcouButton;
        private EcButton trsButton, jtrtButton, aurButton, printStripButton, cmcButton, fmfButton, printFdrButton;
        private EcButton rteButton, clearButton, tpcfButton, enterButton, escapeButton;
        private EcButton lowerButton;
        private EcLabel counterLabel, stateLabel, updateTimeLabel, errorLabel;
        private Panel errorPanel;

        // ---- title bar (drawn in the non-client area) -----------------------------
        private Rectangle closeBox;
        private bool closePressed;

        // ---- fields -------------------------------------------------------------
        private EcField lplField, csecField, pssrField, assrField, opdataField, atdField;
        private EcField acidField, frulField, typ1Field, typ2Field, typ3Field, adepField, etdField, adesField, rflField;
        private EcField rteField, otherField;
        private EcField ftypField, equip1Field, equip2Field, tasField, eetField, altn1Field, altn2Field, regnField;

        private EcField[] planFields;

        public static void Open(FDP2.FDR fdr)
        {
            var window = new EurocatFlightPlanWindow();
            if (fdr != null)
            {
                window.LoadFDR(fdr);
            }

            Form owner = Internals.MainForm;
            if (owner != null && !owner.IsDisposed)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }

            // Draw everything before returning rather than leaving it to the message queue.
            window.FlushPaint(true);
        }

        public EurocatFlightPlanWindow()
        {
            SuspendLayout();
            Name = "EurocatFlightPlanWindow";
            Text = "FLIGHT PLAN WINDOW";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            StartPosition = FormStartPosition.CenterScreen;
            Resizeable = false;
            BackColor = EcTheme.Background;
            MiddleClickClose = false;

            // The title bar is drawn Eurocat-style (MOVE / title / CLOSE) by the overrides below, which
            // replace vatSys' own title-bar buttons. HasCloseButton must stay true all the same:
            // BaseForm cancels every close request for a window that has it switched off.
            HasCloseButton = true;
            HasMinimizeButton = false;
            HasMaximizeButton = false;
            HasSendBackButton = false;
            TitleTextColor = EcTheme.Title;
            BorderColor = EcTheme.Title;

            BuildLayout();
            ResumeLayout(false);

            FDP2.FDRsChanged += FDP2_FDRsChanged;
            Network.ValidATCChanged += Network_ValidATCChanged;
            MMI.SelectedTrackChanged += MMI_SelectedTrackChanged;

            LoadFDR(null);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (acidField.Editable)
            {
                acidField.Focus();
            }
            FlushPaint(true);
        }

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        /// <summary>
        /// Paints the window and every child control right now.
        ///
        /// Windows only delivers WM_PAINT when a thread's message queue is otherwise empty, and each
        /// child control needs its own. vatSys' UI thread is rarely idle, so left to the queue the
        /// window fills in piece by piece over seconds. (vatSys' own controls call Refresh() for the
        /// same reason.)
        /// </summary>
        private void FlushPaint(bool everything = false)
        {
            const uint RDW_INVALIDATE = 0x0001, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;
            if (!IsHandleCreated || IsDisposed || !Visible)
            {
                return;
            }
            RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RDW_ALLCHILDREN | RDW_UPDATENOW | (everything ? RDW_INVALIDATE : 0));
        }

        // =====================================================================
        //  Title bar: [MOVE]   FLIGHT PLAN WINDOW   [CLOSE]
        // =====================================================================

        protected override void OnTitleBarPaint(NonClientPaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle area = e.Bounds;
            g.Clear(EcTheme.Background);

            int boxWidth = (int)Math.Ceiling(g.MeasureString("CLOSE", font).Width) + 22;
            var bar = new Rectangle(area.Left, area.Top, area.Width, area.Height - 1);
            var moveBox = new Rectangle(bar.Left, bar.Top, boxWidth, bar.Height);
            closeBox = new Rectangle(bar.Right - boxWidth, bar.Top, boxWidth, bar.Height);

            EcPaint.Outline(g, bar, EcTheme.Title);
            EcPaint.FlatBox(g, moveBox, "MOVE", font, false);
            EcPaint.FlatBox(g, closeBox, "CLOSE", font, closePressed);
            EcPaint.CentredText(g, Text, font, EcTheme.Title,
                new Rectangle(moveBox.Right, bar.Top, closeBox.Left - moveBox.Right, bar.Height));
        }

        protected override int OnNonClientAreaHitTest(Point p)
        {
            if (closeBox.Contains(p))
            {
                // Not caption: clicking CLOSE must not start a window drag. (MOVE is caption, so it drags.)
                return 18;
            }
            return base.OnNonClientAreaHitTest(p);
        }

        protected override void OnNonClientMouseDown(BaseMouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && closeBox.Contains(e.Location))
            {
                closePressed = true;
                InvalidateNonClient();
                e.Handled = true;
            }
            base.OnNonClientMouseDown(e);
        }

        protected override void OnNonClientMouseUp(BaseMouseEventArgs e)
        {
            bool wasPressed = closePressed;
            closePressed = false;
            if (wasPressed)
            {
                InvalidateNonClient();
                if (closeBox.Contains(e.Location))
                {
                    Close();
                    return;
                }
            }
            base.OnNonClientMouseUp(e);
        }

        protected override void OnNonClientMouseLeave(EventArgs e)
        {
            if (closePressed)
            {
                closePressed = false;
                InvalidateNonClient();
            }
            base.OnNonClientMouseLeave(e);
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        /// <summary>LOWER: drop this window behind vatSys' other windows, as the stock send-to-back button does.</summary>
        private void LowerWindow()
        {
            const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
            Form main = Internals.MainForm;
            if (main != null && main.IsHandleCreated)
            {
                SetWindowPos(Handle, main.Handle, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            else
            {
                SendToBack();
            }
        }

        // =====================================================================
        //  Layout
        // =====================================================================

        private void BuildLayout()
        {
            int y = Pad;

            Panel grid = BuildButtonGrid();
            Panel row1 = BuildSystemRow();
            Panel row2 = BuildFlightRow();
            Panel row5 = BuildEquipmentRow();

            // The ACID row sits slightly indented from the rows above and below it.
            const int flightRowIndent = 16;
            int width = new[] { grid.Width, row1.Width, row2.Width + flightRowIndent, row5.Width }.Max();

            grid.Location = new Point(Pad, y);
            Controls.Add(grid);
            y = grid.Bottom + 6;

            var separator = new EcSeparator
            {
                Location = new Point(0, y),
                Width = width + Pad * 2
            };
            Controls.Add(separator);
            y = separator.Bottom + 6;

            row1.Location = new Point(Pad, y);
            Controls.Add(row1);
            y = row1.Bottom + 2;

            row2.Location = new Point(Pad + flightRowIndent, y);
            Controls.Add(row2);
            y = row2.Bottom + 6;

            // ---- RTE ----------------------------------------------------------
            const int leftCol = 54;
            const int scrollW = 13;
            int lineH = TextRenderer.MeasureText("W", font).Height;
            int wideW = width - leftCol - scrollW - 4;

            rteButton = MakeButton("RTE", 44, 26);
            rteButton.Location = new Point(Pad, y + 2);
            rteButton.Click += (s, e) => Guard(ToggleGraphicRoute);
            Controls.Add(rteButton);

            rteField = MakeMultilineField(wideW, lineH * 3 + 12);
            rteField.Location = new Point(Pad + leftCol, y);
            Controls.Add(rteField);
            AddScrollBar(rteField, Pad + leftCol + wideW + 4);
            y = rteField.Bottom + 4;

            // ---- OTHER --------------------------------------------------------
            otherField = MakeMultilineField(wideW, lineH * 2 + 12);
            otherField.Location = new Point(Pad + leftCol, y);
            EcLabel otherLabel = MakeLabel("OTHER");
            otherLabel.Location = new Point(Pad, y + 6);
            Controls.Add(otherLabel);
            Controls.Add(otherField);
            AddScrollBar(otherField, Pad + leftCol + wideW + 4);
            y = otherField.Bottom + 4;

            row5.Location = new Point(Pad, y);
            Controls.Add(row5);
            y = row5.Bottom + 8;

            // ---- message area ---------------------------------------------------
            errorPanel = new Panel
            {
                Location = new Point(Pad, y),
                Size = new Size(width, lineH * 2 + 10),
                BackColor = BackColor
            };
            errorLabel = new EcLabel("", font)
            {
                Dock = DockStyle.Fill,
                Visible = false
            };
            errorPanel.Controls.Add(errorLabel);
            Controls.Add(errorPanel);
            y = errorPanel.Bottom + 6;

            // ---- bottom row -----------------------------------------------------
            var bottomSeparator = new EcSeparator
            {
                Location = new Point(0, y),
                Width = width + Pad * 2
            };
            Controls.Add(bottomSeparator);
            y = bottomSeparator.Bottom + 8;

            clearButton = MakeButton("Clear", 64, 26);
            clearButton.Location = new Point(Pad + 6, y);
            clearButton.Click += (s, e) => Guard(ClearAll);

            tpcfButton = MakeButton("TPCF", 64, 26);
            tpcfButton.Location = new Point(Pad + 6 + 64 + 90, y);
            tpcfButton.Enabled = false;

            escapeButton = MakeButton("Escape", 64, 26);
            escapeButton.Location = new Point(Pad + width - 6 - 64, y);
            escapeButton.Click += (s, e) => Guard(EscapePressed);

            enterButton = MakeButton("Enter", 64, 26);
            enterButton.Location = new Point(escapeButton.Left - 8 - 64, y);
            enterButton.Click += (s, e) => Guard(EnterPressed);

            Controls.AddRange(new Control[] { clearButton, tpcfButton, enterButton, escapeButton });
            y = clearButton.Bottom + 10;

            // ---- footer: [LOWER] ------------------------------------------------
            const int footerH = 22;
            var footerLine = new Panel
            {
                BackColor = EcTheme.Title,
                Location = new Point(0, y),
                Size = new Size(width + Pad * 2, 1)
            };
            Controls.Add(footerLine);

            lowerButton = MakeButton("LOWER", TextRenderer.MeasureText("CLOSE", font).Width + 16, footerH);
            lowerButton.Flat = true;
            lowerButton.Location = new Point(0, y + 1);
            lowerButton.Click += (s, e) => Guard(LowerWindow);
            Controls.Add(lowerButton);
            y += footerH + 1;

            ClientSize = new Size(width + Pad * 2, y);

            planFields = new[]
            {
                frulField, typ1Field, typ2Field, typ3Field, adepField, etdField, adesField, rflField,
                rteField, otherField, equip1Field, equip2Field, tasField, eetField, altn1Field, regnField
            };

            // Keyboard order follows the form top-to-bottom, left-to-right. Fields sit inside row
            // panels, so the rows are ordered first and the fields within each row second.
            int tab = 0;
            foreach (Control c in new Control[] { row1, row2, rteField, otherField, row5 })
            {
                c.TabIndex = tab++;
            }
        }

        private Panel BuildButtonGrid()
        {
            var panel = new Panel();

            // Column x-positions: groups are separated by a wider gap.
            int[] groupSizes = { 1, 4, 3, 1, 2 };
            var colX = new List<int>();
            int x = 0;
            foreach (int size in groupSizes)
            {
                for (int i = 0; i < size; i++)
                {
                    colX.Add(x);
                    x += BtnW + BtnGap;
                }
                x += GroupGap - BtnGap;
            }
            int timeX = x;

            Point Cell(int col, int row) => new Point(colX[col], row * (BtnH + BtnGap));

            EcButton Add(string text, int col, int row, Action onClick = null)
            {
                EcButton b = MakeButton(text, BtnW, BtnH);
                b.Location = Cell(col, row);
                if (onClick != null)
                {
                    b.Click += (s, e) => Guard(onClick);
                }
                else
                {
                    // No vatSys equivalent - shown for layout fidelity only.
                    b.Enabled = false;
                }
                panel.Controls.Add(b);
                return b;
            }

            EcButton AddToggle(string text, int col, int row, EventHandler onChanged)
            {
                EcButton b = MakeButton(text, BtnW, BtnH);
                b.Toggle = true;
                b.Location = Cell(col, row);
                b.CheckedChanged += onChanged;
                panel.Controls.Add(b);
                return b;
            }

            // group 0: paging
            nextButton = Add("NEXT", 0, 0);
            prevButton = Add("PREV", 0, 1);
            counterLabel = MakeLabel("0/0", BtnW, BtnH);
            counterLabel.Location = Cell(0, 2);
            panel.Controls.Add(counterLabel);

            // group 1: plan handling
            creButton = AddToggle("CRE", 1, 0, CreButton_CheckedChanged);
            rplButton = Add("RPL", 2, 0);
            estButton = Add("EST", 3, 0, EstClicked);
            holdButton = Add("HOLD", 4, 0);
            crteButton = Add("CRTE", 1, 1);
            steButton = Add("STE", 2, 1);
            modButton = AddToggle("MOD", 3, 1, ModButton_CheckedChanged);
            backButton = Add("BACK", 4, 1, BackClicked);
            stateLabel = MakeLabel("", BtnW, BtnH);
            stateLabel.Location = Cell(1, 2);
            panel.Controls.Add(stateLabel);
            prlButton = Add("PRL", 2, 2, PrlClicked);
            tmaButton = Add("TMA", 3, 2);
            cflButton = Add("CFL", 4, 2, CflClicked);

            // group 2: control
            depButton = AddToggle("DEP", 5, 0, DepButton_CheckedChanged);
            accButton = Add("ACC", 6, 0, AccClicked);
            cnlButton = Add("CNL", 7, 0, CnlClicked);
            cbepButton = Add("CBEP", 5, 1);
            hndButton = Add("HND", 6, 1, HndClicked);
            inhiButton = Add("INHI", 7, 1, InhiClicked);
            exitButton = Add("EXIT", 5, 2);
            hndiButton = Add("HND/I", 6, 2);
            supButton = Add("SUP", 7, 2);

            // group 3: coupling
            ncouButton = Add("NCOU", 8, 0, NcouClicked);
            bcouButton = Add("BCOU", 8, 1, BcouClicked);

            // group 4: misc (no vatSys equivalent)
            trsButton = Add("TRS", 9, 0);
            jtrtButton = Add("JTRT", 10, 0);
            aurButton = Add("AUR", 9, 1);
            printStripButton = Add("PRINT\nSTRIP", 10, 1);
            cmcButton = Add("CMC", 9, 2);
            fmfButton = Add("FMF", 10, 2);

            // update time column
            EcLabel updateCaption = MakeLabel("Update Time", TimeColW, BtnH / 2 + 4);
            updateCaption.Location = new Point(timeX, 2);
            panel.Controls.Add(updateCaption);

            updateTimeLabel = MakeLabel("00:00:00", TimeColW, BtnH / 2 + 4);
            updateTimeLabel.Location = new Point(timeX, updateCaption.Bottom);
            panel.Controls.Add(updateTimeLabel);

            printFdrButton = MakeButton("PRINT\nFDR", BtnW, BtnH);
            printFdrButton.Location = new Point(timeX + (TimeColW - BtnW) / 2, 2 * (BtnH + BtnGap));
            printFdrButton.Enabled = false;
            panel.Controls.Add(printFdrButton);

            panel.Size = new Size(timeX + TimeColW, 3 * BtnH + 2 * BtnGap);
            return panel;
        }

        private Panel BuildSystemRow()
        {
            var row = new RowBuilder(this);
            lplField = row.Field("LPL", 3);
            csecField = row.Field("CSEC", 4);
            pssrField = row.Field("PSSR", 4);
            assrField = row.Field("ASSR", 4, maxLength: 4);
            assrField.OctalOnly = true;
            opdataField = row.Field("OPDATA", 10);
            atdField = row.Field("ATD", 4, maxLength: 4, numeric: true);
            return row.Finish();
        }

        private Panel BuildFlightRow()
        {
            var row = new RowBuilder(this);
            acidField = row.Field("ACID", 7, maxLength: 7);
            frulField = row.Field("FRUL", 1, maxLength: 1);
            typ1Field = row.Field("TYP", 2, maxLength: 2, numeric: true);
            typ2Field = row.Field("/", 4, maxLength: 4, tight: true);
            typ3Field = row.Field("/", 1, maxLength: 1, tight: true);
            adepField = row.Field("ADEP", 4, maxLength: 4);
            etdField = row.Field("ETD", 4, maxLength: 4, numeric: true);
            adesField = row.Field("ADES", 4, maxLength: 4);
            rflField = row.Field("RFL", 3, maxLength: 3);
            return row.Finish();
        }

        private Panel BuildEquipmentRow()
        {
            var row = new RowBuilder(this);
            ftypField = row.Field("FTYP", 1, maxLength: 1);
            equip1Field = row.Field("EQUIP", 10);
            equip2Field = row.Field("/", 4, tight: true);
            tasField = row.Field("TAS", 4, maxLength: 4, numeric: true);
            eetField = row.Field("EET", 4, maxLength: 4, numeric: true);
            altn1Field = row.Field("ALTN", 4, maxLength: 4);
            altn2Field = row.Field(null, 4, maxLength: 4, tight: true);
            regnField = row.Field("REGN", 7, maxLength: 7);
            return row.Finish();
        }

        /// <summary>Lays out "LABEL [field]" pairs left to right in an auto-sized panel.</summary>
        private sealed class RowBuilder
        {
            private readonly EurocatFlightPlanWindow owner;
            private readonly Panel panel = new Panel();
            private int x;
            private int height;

            public RowBuilder(EurocatFlightPlanWindow owner)
            {
                this.owner = owner;
            }

            public EcField Field(string label, int chars, int maxLength = 0, bool numeric = false, bool tight = false)
            {
                var field = new EcField(owner.font) { NumericOnly = numeric };
                if (maxLength > 0)
                {
                    field.MaxLength = maxLength;
                }
                field.SizeForChars(chars);
                height = Math.Max(height, field.Height);

                if (x > 0)
                {
                    x += tight ? 0 : 8;
                }

                if (!string.IsNullOrEmpty(label))
                {
                    EcLabel l = owner.MakeLabel(label);
                    l.Location = new Point(x, 0);
                    panel.Controls.Add(l);
                    x += l.Width;
                }

                field.Location = new Point(x, 0);
                field.TabIndex = panel.Controls.Count;
                panel.Controls.Add(field);
                x += field.Width;
                return field;
            }

            public Panel Finish()
            {
                foreach (Control c in panel.Controls)
                {
                    if (c is EcLabel)
                    {
                        c.Height = height;
                    }
                }
                panel.Size = new Size(x, height);
                return panel;
            }
        }

        private EcLabel MakeLabel(string text, int width = 0, int height = 0)
        {
            Size measured = TextRenderer.MeasureText(text.Length == 0 ? "W" : text, font);
            return new EcLabel(text, font)
            {
                Size = new Size(width > 0 ? width : measured.Width, height > 0 ? height : measured.Height + 4)
            };
        }

        private EcButton MakeButton(string text, int width, int height)
        {
            return new EcButton
            {
                Text = text,
                Font = font,
                Size = new Size(width, height)
            };
        }

        private EcField MakeMultilineField(int width, int height)
        {
            return new EcField(font, multiline: true)
            {
                Size = new Size(width, height)
            };
        }

        /// <summary>Scroll strip to the right of a multi-line field, inset to line up with the field's box.</summary>
        private void AddScrollBar(EcField target, int x)
        {
            Controls.Add(new EcScrollBar(target)
            {
                Location = new Point(x, target.Top + 2),
                Size = new Size(13, target.Height - 4)
            });
        }

        // =====================================================================
        //  Loading / state
        // =====================================================================

        public void LoadFDR(FDP2.FDR fdr)
        {
            Unsubscribe();
            HideGraphicRoute();
            CancelPendingRequest();

            _fdr = fdr;
            _mode = Mode.View;
            if (_fdr != null)
            {
                acidField.Text = _fdr.Callsign;
                _fdr.PropertyChanged += Fdr_PropertyChanged;
            }

            LoadFieldData(false);
            StampUpdateTime();
            RefreshState();
        }

        private void Unsubscribe()
        {
            if (_fdr != null)
            {
                _fdr.PropertyChanged -= Fdr_PropertyChanged;
            }
        }

        /// <summary>Copies FDR data into the fields. When <paramref name="keepEdits"/> is set, fields the user is typing in are left alone.</summary>
        private void LoadFieldData(bool keepEdits)
        {
            void Set(EcField field, string value)
            {
                if (keepEdits && field.Editable && field.Modified)
                {
                    return;
                }
                if (field.Text != (value ?? ""))
                {
                    field.Text = value ?? "";
                }
                field.Modified = false;
            }

            FDP2.FDR f = _fdr;
            if (f == null)
            {
                foreach (EcField field in AllFieldsExceptAcid())
                {
                    Set(field, "");
                }
                return;
            }

            Set(frulField, f.FlightRules);
            Set(typ1Field, f.AircraftCount.ToString());
            Set(typ2Field, f.AircraftType);
            Set(typ3Field, f.AircraftWake);
            Set(equip1Field, f.AircraftEquip);
            Set(equip2Field, f.AircraftSurvEquip);
            Set(adepField, f.DepAirport);
            Set(etdField, f.ETD.ToString("HHmm"));
            Set(tasField, f.TAS.ToString());
            Set(rflField, (f.RFL / 100).ToString("D3"));
            Set(adesField, f.DesAirport);
            Set(eetField, f.EET.ToString("hhmm"));
            Set(altn1Field, f.AltAirport);
            Set(altn2Field, "");
            Set(rteField, f.Route);
            Set(otherField, f.Remarks);
            Set(regnField, ParseRegistration(f.Remarks));
            Set(ftypField, "");
            Set(lplField, "");
            Set(assrField, f.AssignedSSRCode != -1 && SquawkAssignment.IsCodeDiscrete(f.AssignedSSRCode)
                ? Convert.ToString(f.AssignedSSRCode, 8).PadLeft(4, '0')
                : "");
            Set(opdataField, f.LabelOpData);
            Set(atdField, f.ATD != DateTime.MaxValue ? f.ATD.ToString("HHmm") : "");

            string sector = f.ControllingSector?.Name ?? "";
            Set(csecField, sector.Length > 3 ? sector.Substring(0, 3) + "*" : sector);

            string squawk = "";
            try
            {
                var pilot = f.CoupledTrack?.ActualAircraft;
                if (pilot != null)
                {
                    squawk = Convert.ToString(pilot.TransponderCode, 8).PadLeft(4, '0');
                }
            }
            catch
            {
            }
            Set(pssrField, squawk);
        }

        private IEnumerable<EcField> AllFieldsExceptAcid()
        {
            return new[]
            {
                lplField, csecField, pssrField, assrField, opdataField, atdField, frulField, typ1Field, typ2Field, typ3Field,
                adepField, etdField, adesField, rflField, rteField, otherField, ftypField, equip1Field, equip2Field, tasField,
                eetField, altn1Field, altn2Field, regnField
            };
        }

        private void StampUpdateTime()
        {
            updateTimeLabel.Text = _fdr == null ? "00:00:00" : DateTime.UtcNow.ToString("HH:mm:ss");
        }

        private static string StateText(States state)
        {
            switch (state)
            {
                case States.STATE_INACTIVE: return "NACT";
                case States.STATE_FINISHED: return "FIN";
                case States.STATE_HANDOVER:
                case States.STATE_HANDOVER_FIRST: return "HND";
                case States.STATE_INHIBITED: return "INHI";
                case States.STATE_SUSPENDED: return "SUS";
                case States.STATE_UNCONTROLLED: return "UCTL";
                case States.STATE_PREACTIVE: return "PREA";
                case States.STATE_COORDINATED: return "COOR";
                case States.STATE_CONTROLLED: return "CONT";
                default: return "";
            }
        }

        /// <summary>Whether the label/system data (ASSR, OPDATA, PRL, CFL) may be edited - mirrors the stock window's "system" button.</summary>
        private bool CanEditSystemData()
        {
            if (_fdr == null || !_fdr.HavePermission)
            {
                return false;
            }
            switch (_fdr.State)
            {
                case States.STATE_HANDOVER:
                case States.STATE_HANDOVER_FIRST:
                case States.STATE_INHIBITED:
                case States.STATE_PREACTIVE:
                case States.STATE_COORDINATED:
                case States.STATE_CONTROLLED:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Single place that derives every enabled/checked state from (mode, FDR, network).</summary>
        private void RefreshState()
        {
            _updating = true;
            try
            {
                bool has = _fdr != null;
                bool valid = Network.ValidATC;
                bool perm = has && _fdr.HavePermission;
                States state = has ? _fdr.State : States.STATE_NULL;
                bool view = _mode == Mode.View && !_requestPending;

                bool canCreate = valid;
                bool canMod = false, canCnl = false, canEst = false, canBack = false, canDep = false;
                bool canAcc = false, canHnd = false, canInhi = false;

                if (has)
                {
                    bool own = valid && (perm || !_fdr.IsTracked);
                    canCreate = own;
                    canMod = own;
                    canCnl = own;

                    switch (state)
                    {
                        case States.STATE_INACTIVE:
                            canEst = true;
                            canCnl = false;
                            break;
                        case States.STATE_FINISHED:
                            canMod = false;
                            canCnl = false;
                            break;
                        case States.STATE_HANDOVER:
                        case States.STATE_HANDOVER_FIRST:
                            canAcc = valid;
                            canCreate = false;
                            canInhi = perm;
                            break;
                        case States.STATE_INHIBITED:
                            canInhi = true;
                            canAcc = valid;
                            canCreate = false;
                            break;
                        case States.STATE_SUSPENDED:
                            canCreate = false;
                            break;
                        case States.STATE_UNCONTROLLED:
                            canAcc = valid;
                            canCreate = false;
                            break;
                        case States.STATE_PREACTIVE:
                            canEst = true;
                            canCreate = false;
                            break;
                        case States.STATE_COORDINATED:
                            canDep = true;
                            canBack = _fdr.ATD == DateTime.MaxValue;
                            canCreate = false;
                            canInhi = perm;
                            break;
                        case States.STATE_CONTROLLED:
                            canDep = perm;
                            canHnd = perm;
                            canCreate = false;
                            canInhi = perm;
                            break;
                    }
                }

                bool sys = CanEditSystemData();

                // ---- function buttons ----
                creButton.Checked = _mode == Mode.Create;
                modButton.Checked = _mode == Mode.Modify;
                depButton.Checked = _mode == Mode.Depart;

                creButton.Enabled = _mode == Mode.Create || (view && canCreate);
                modButton.Enabled = _mode == Mode.Modify || (view && canMod);
                depButton.Enabled = _mode == Mode.Depart || (view && canDep);

                estButton.Enabled = view && canEst;
                backButton.Enabled = view && canBack;
                cnlButton.Enabled = view && canCnl;
                accButton.Enabled = view && canAcc;
                hndButton.Enabled = view && canHnd;
                inhiButton.Enabled = view && canInhi && valid;
                prlButton.Enabled = view && sys;
                cflButton.Enabled = view && sys;
                rteButton.Enabled = has && _mode == Mode.View;
                enterButton.Enabled = !_requestPending;

                UpdateCoupleButtons(view);

                // ---- fields ----
                bool editingPlan = _mode == Mode.Create || _mode == Mode.Modify;
                foreach (EcField field in planFields)
                {
                    SetFieldEnabled(field, editingPlan);
                }

                SetFieldEnabled(acidField, _mode == Mode.View);
                SetFieldEnabled(atdField, _mode == Mode.Depart);
                SetFieldEnabled(assrField, (view && sys) || _mode == Mode.Create);
                SetFieldEnabled(opdataField, (view && sys) || _mode == Mode.Create);

                // Display-only (no vatSys data behind them, or derived).
                SetFieldEnabled(lplField, false);
                SetFieldEnabled(csecField, false);
                SetFieldEnabled(pssrField, false);
                SetFieldEnabled(ftypField, false);
                SetFieldEnabled(altn2Field, false);

                stateLabel.Text = has ? StateText(state) : "";
                counterLabel.Text = has ? "1/1" : "0/0";
            }
            finally
            {
                _updating = false;
                FlushPaint();
            }
        }

        private void SetFieldEnabled(EcField field, bool enabled)
        {
            // Editable fields draw as sunken boxes, display-only ones as a thin outline.
            field.Editable = enabled;
        }

        private void UpdateCoupleButtons(bool view)
        {
            bool canDecouple = false;
            bool canCouple = false;

            if (_fdr != null && view)
            {
                if (_fdr.CoupledTrack != null)
                {
                    canDecouple = true;
                }
                else
                {
                    Track selected = MMI.SelectedTrack;
                    bool selectedIsFreeRadarTrack = selected != null
                                                    && selected.Type == Track.TrackTypes.TRACK_TYPE_RADAR
                                                    && selected.GetFDR() == null;
                    canCouple = selectedIsFreeRadarTrack || _fdr.InhibitAutoCouple;
                }
            }

            ncouButton.Enabled = canDecouple;
            bcouButton.Enabled = canCouple;
        }

        // =====================================================================
        //  vatSys events
        // =====================================================================

        private void OnGui(Action action)
        {
            if (IsDisposed)
            {
                return;
            }
            if (IsHandleCreated && InvokeRequired)
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    if (!IsDisposed)
                    {
                        action();
                    }
                }));
            }
            else
            {
                action();
            }
        }

        private void Fdr_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != null && !WatchedProperties.Contains(e.PropertyName))
            {
                return;
            }

            OnGui(() =>
            {
                if (_fdr == null || sender != _fdr || _mode != Mode.View)
                {
                    return;
                }
                LoadFieldData(true);
                StampUpdateTime();
                RefreshState();
            });
        }

        private void FDP2_FDRsChanged(object sender, FDP2.FDRsChangedEventArgs e)
        {
            FDP2.FDR current = _fdr;
            if (current == null || e.Change != current || FDP2.GetFDRs.Contains(current))
            {
                return;
            }

            // Our FDR was deleted.
            OnGui(() =>
            {
                if (_fdr == current)
                {
                    LoadFDR(null);
                }
            });
        }

        private void Network_ValidATCChanged(object sender, EventArgs e)
        {
            OnGui(() => LoadFDR(null));
        }

        private void MMI_SelectedTrackChanged(object sender, EventArgs e)
        {
            if (_fdr == null)
            {
                return;
            }
            OnGui(() => UpdateCoupleButtons(_mode == Mode.View && !_requestPending));
        }

        // =====================================================================
        //  Mode buttons
        // =====================================================================

        private void CreButton_CheckedChanged(object sender, EventArgs e)
        {
            if (_updating)
            {
                return;
            }

            Guard(() =>
            {
                if (!creButton.Checked)
                {
                    LeaveEditMode();
                    return;
                }

                string acid = acidField.Text.Trim().ToUpperInvariant();
                if (acid == "")
                {
                    ShowError(new FDRParseException(FDRParseException.NOT_VALID_CALLSIGN, acidField.Text));
                    RefreshState();
                    return;
                }

                bool sameFlight = _fdr != null && _fdr.Callsign == acid;
                if (sameFlight && !_fdr.HavePermission)
                {
                    ShowError(new FDRParseException(FDRParseException.CONT_FP_EXISTS, acid));
                    RefreshState();
                    return;
                }

                List<NetworkPilot> pilots = Network.GetOnlinePilots;
                if (pilots == null || !pilots.Any(p => (p.Callsign ?? "").ToUpperInvariant().Trim() == acid))
                {
                    ShowError(new FDRParseException(FDRParseException.CALLSIGN_DOES_NOT_EXIST, acid));
                    RefreshState();
                    return;
                }

                if (!sameFlight && _fdr != null)
                {
                    // A different callsign was typed over a loaded plan: start from blank rather than
                    // silently copying the other aircraft's plan.
                    Unsubscribe();
                    HideGraphicRoute();
                    _fdr = null;
                    LoadFieldData(false);
                    StampUpdateTime();
                }

                acidField.Text = acid;
                _mode = Mode.Create;
                ClearError();
                RefreshState();

                etdField.Text = "";
                assrField.Text = "";
                opdataField.Text = "";
                atdField.Text = "";
                SetDefault(typ1Field, "1");
                SetDefault(frulField, "I");
                SetDefault(equip1Field, "G");
                SetDefault(equip2Field, "C");
                typ2Field.Focus();
            });
        }

        private static void SetDefault(EcField field, string value)
        {
            if (field.Text.Trim() == "" || field.Text.Trim() == "0")
            {
                field.Text = value;
            }
        }

        private void ModButton_CheckedChanged(object sender, EventArgs e)
        {
            if (_updating)
            {
                return;
            }

            Guard(() =>
            {
                if (!modButton.Checked)
                {
                    LeaveEditMode();
                    return;
                }

                if (_fdr == null)
                {
                    RefreshState();
                    return;
                }
                if (!_fdr.HavePermission)
                {
                    ShowError(new FDRParseException(FDRParseException.CONT_FP_EXISTS, _fdr.Callsign));
                    RefreshState();
                    return;
                }

                acidField.Text = _fdr.Callsign;
                LoadFieldData(false);
                _mode = Mode.Modify;
                ClearError();
                RefreshState();
                rteField.Focus();
            });
        }

        private void DepButton_CheckedChanged(object sender, EventArgs e)
        {
            if (_updating)
            {
                return;
            }

            Guard(() =>
            {
                if (!depButton.Checked)
                {
                    LeaveEditMode();
                    return;
                }

                if (_fdr == null)
                {
                    RefreshState();
                    return;
                }

                _mode = Mode.Depart;
                ClearError();
                RefreshState();
                atdField.Focus();
                atdField.SelectAll();
            });
        }

        /// <summary>Abandons Create/Modify/Depart and shows the stored plan again.</summary>
        private void LeaveEditMode()
        {
            ClearError();
            LoadFDR(_fdr != null && FDP2.GetFDRs.Contains(_fdr) ? _fdr : null);
        }

        // =====================================================================
        //  Enter / Escape / Clear
        // =====================================================================

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // The buttons never take focus, so Enter/Escape are routed here rather than through
            // AcceptButton/CancelButton.
            if (keyData == Keys.Return)
            {
                if (enterButton.Enabled)
                {
                    Guard(EnterPressed);
                }
                return true;
            }
            if (keyData == Keys.Escape)
            {
                Guard(EscapePressed);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void EnterPressed()
        {
            switch (_mode)
            {
                case Mode.Modify:
                    CommitModify();
                    break;
                case Mode.Create:
                    CommitCreate();
                    break;
                case Mode.Depart:
                    CommitDepart();
                    break;
                default:
                    EnterInViewMode();
                    break;
            }
        }

        private void EscapePressed()
        {
            ClearError();
            if (_mode != Mode.View)
            {
                LeaveEditMode();
            }
            else
            {
                string acid = acidField.Text;
                LoadFDR(null);
                acidField.Text = acid;
            }
            acidField.Focus();
        }

        private void ClearAll()
        {
            ClearError();
            LoadFDR(null);
            acidField.Text = "";
            acidField.Focus();
        }

        private static string OneLine(string text)
        {
            return Regex.Replace(text ?? "", @"\s*[\r\n]+\s*", " ").Trim();
        }

        private void CommitModify()
        {
            FDP2.FDR fdr = _fdr;
            if (fdr == null)
            {
                LeaveEditMode();
                return;
            }
            if (!fdr.HavePermission)
            {
                ShowError(new FDRParseException(FDRParseException.CONT_FP_EXISTS, "CSEC " + fdr.ControllerTracking?.Callsign));
                return;
            }

            try
            {
                FDP2.ModifyFDR(fdr, fdr.Callsign, frulField.Text, adepField.Text, adesField.Text, OneLine(rteField.Text),
                    RemarksWithRegistration(), typ1Field.Text, typ2Field.Text, typ3Field.Text, equip1Field.Text, equip2Field.Text,
                    tasField.Text, rflField.Text, etdField.Text, eetField.Text, atdField.Text, altn1Field.Text);
            }
            catch (FDRParseException ex)
            {
                ShowError(ex);
                return;
            }

            Internals.SendFlightPlanChange(fdr);
            ClearError();
            LoadFDR(fdr);
        }

        private void CommitCreate()
        {
            string acid = acidField.Text.Trim().ToUpperInvariant();
            bool existed = FDP2.GetFDRIndex(acid) != -1;

            try
            {
                FDP2.CreateFDR(acid, frulField.Text, adepField.Text, adesField.Text, OneLine(rteField.Text), RemarksWithRegistration(),
                    typ1Field.Text, typ2Field.Text, typ3Field.Text, equip1Field.Text, equip2Field.Text, tasField.Text, rflField.Text,
                    etdField.Text, eetField.Text, "", altn1Field.Text);
            }
            catch (FDRParseException ex)
            {
                ShowError(ex);
                return;
            }

            FDP2.FDR created = FDP2.GetFDRs.LastOrDefault(f => f.Callsign == acid);
            if (created == null)
            {
                ShowError("'" + acid + "' : FDR was not created");
                return;
            }

            if (existed)
            {
                Internals.SendFlightPlanChange(created);
            }
            else
            {
                Internals.SendNewFlightPlan(created);
            }

            // ASSR / OPDATA typed while creating are applied to the new plan.
            string problem = null;
            string ssr = assrField.Text.Trim();
            string opdata = opdataField.Text.Trim();
            if (ssr != "")
            {
                try
                {
                    FDP2.SetASSR(created, Convert.ToInt32(ssr, 8));
                }
                catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is OverflowException)
                {
                    problem = ex is ArgumentException ? ex.Message : "Invalid SSR code";
                }
            }
            if (opdata != "")
            {
                FDP2.SetLabelData(created, opdata);
            }

            ClearError();
            LoadFDR(created);
            if (problem != null)
            {
                ShowError(problem);
            }
        }

        private void CommitDepart()
        {
            FDP2.FDR fdr = _fdr;
            if (fdr == null)
            {
                LeaveEditMode();
                return;
            }

            try
            {
                if (atdField.Text.Length == 0)
                {
                    if (fdr.ATD != DateTime.MaxValue)
                    {
                        FDP2.CancelFDRDeparture(fdr);
                    }
                    else
                    {
                        FDP2.DepartFDR(fdr, DateTime.UtcNow);
                    }
                }
                else
                {
                    FDP2.DepartFDR(fdr, atdField.Text);
                }
            }
            catch
            {
                ShowError("Unable to parse ATD");
                return;
            }

            ClearError();
            LoadFDR(fdr);
        }

        private void EnterInViewMode()
        {
            string acid = acidField.Text.Trim().ToUpperInvariant();
            if (acid == "")
            {
                return;
            }

            if (_fdr != null && _fdr.Callsign == acid)
            {
                if (CanEditSystemData())
                {
                    CommitSystemData();
                }
                return;
            }

            int index = FDP2.GetFDRIndex(acid);
            if (index != -1)
            {
                FDP2.FDR found = FDP2.GetFDRs.FirstOrDefault(f => f.Callsign == acid);
                if (found != null)
                {
                    ClearError();
                    LoadFDR(found);
                    return;
                }
            }

            RequestFlightPlan(acid);
        }

        /// <summary>Applies ASSR / OPDATA edits made directly in the window.</summary>
        private void CommitSystemData()
        {
            FDP2.FDR fdr = _fdr;

            if (assrField.Modified)
            {
                string text = assrField.Text.Trim();
                if (text.Length == 0)
                {
                    FDP2.SetASSR(fdr);
                }
                else
                {
                    int code;
                    try
                    {
                        code = Convert.ToInt32(text, 8);
                    }
                    catch
                    {
                        ShowError("Invalid SSR code");
                        return;
                    }

                    if (fdr.AssignedSSRCode != code)
                    {
                        try
                        {
                            FDP2.SetASSR(fdr, code);
                        }
                        catch (ArgumentException ex)
                        {
                            ShowError(ex.Message);
                            return;
                        }
                    }
                }
            }

            if (opdataField.Modified && opdataField.Text != fdr.LabelOpData)
            {
                FDP2.SetLabelData(fdr, opdataField.Text);
            }

            ClearError();
            assrField.Modified = false;
            opdataField.Modified = false;
            LoadFieldData(false);
            StampUpdateTime();
            RefreshState();
        }

        /// <summary>No local FDR: ask the network for the plan, like the stock window does.</summary>
        private void RequestFlightPlan(string acid)
        {
            CancelPendingRequest();
            Unsubscribe();
            HideGraphicRoute();
            _fdr = null;
            LoadFieldData(false);
            ClearError();

            _requestPending = true;
            RefreshState();

            _requestTimer = new Timer { Interval = 3000 };
            _requestTimer.Tick += (s, e) => FinishRequest(acid, false);

            _unsubscribeNoFp = Internals.SubscribeNoFPExists(callsign =>
            {
                if (callsign != null && callsign.Trim().ToUpperInvariant() == acid)
                {
                    OnGui(() => FinishRequest(acid, true));
                }
            });

            try
            {
                Internals.SendFPRequest(acid);
            }
            catch
            {
                CancelPendingRequest();
                RefreshState();
                throw;
            }
            _requestTimer.Start();
        }

        private void FinishRequest(string acid, bool definitelyMissing)
        {
            if (!_requestPending)
            {
                return;
            }
            CancelPendingRequest();

            FDP2.FDR found = definitelyMissing ? null : FDP2.GetFDRs.FirstOrDefault(f => f.Callsign == acid);
            if (found != null)
            {
                ClearError();
                LoadFDR(found);
            }
            else
            {
                RefreshState();
                ShowError("'" + acid + "' : no FDR exists");
            }
        }

        private void CancelPendingRequest()
        {
            _requestPending = false;
            if (_requestTimer != null)
            {
                _requestTimer.Stop();
                _requestTimer.Dispose();
                _requestTimer = null;
            }
            if (_unsubscribeNoFp != null)
            {
                _unsubscribeNoFp();
                _unsubscribeNoFp = null;
            }
        }

        // =====================================================================
        //  Registration (REGN <-> REG/ in remarks)
        // =====================================================================

        private static readonly Regex RegRegex = new Regex(@"(?<![A-Z0-9])REG/(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static string ParseRegistration(string remarks)
        {
            Match m = RegRegex.Match(remarks ?? "");
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
        }

        /// <summary>Remarks text with the REG/ item brought in line with the REGN field.</summary>
        internal static string ApplyRegistration(string remarks, string regn)
        {
            remarks = remarks ?? "";
            regn = (regn ?? "").Trim().ToUpperInvariant();
            if (regn == ParseRegistration(remarks))
            {
                return remarks;
            }

            if (regn == "")
            {
                return Regex.Replace(RegRegex.Replace(remarks, ""), @"\s{2,}", " ").Trim();
            }
            if (RegRegex.IsMatch(remarks))
            {
                return RegRegex.Replace(remarks, "REG/" + regn, 1);
            }
            return (remarks.Trim() + " REG/" + regn).Trim();
        }

        private string RemarksWithRegistration()
        {
            return ApplyRegistration(OneLine(otherField.Text), regnField.Text);
        }

        // =====================================================================
        //  Function buttons
        // =====================================================================

        private void EstClicked()
        {
            FDP2.FDR fdr = _fdr;
            if (fdr == null)
            {
                return;
            }
            if (fdr.ParsedRoute == null || fdr.ParsedRoute.Count == 0)
            {
                ShowError("Cannot activate (NO ROUTE)");
                return;
            }
            Internals.ShowESTWindow(fdr);
            ReloadAfterAction(fdr);
        }

        private void BackClicked()
        {
            FDP2.FDR fdr = _fdr;
            if (fdr == null)
            {
                return;
            }
            FDP2.BackFDR(fdr);
            if (fdr.ParsedRoute != null && fdr.ParsedRoute.Count > 0)
            {
                Internals.SendCancelEST(fdr, fdr.ParsedRoute[0]);
            }
            ReloadAfterAction(fdr);
        }

        private void InhiClicked()
        {
            FDP2.FDR fdr = _fdr;
            if (fdr == null)
            {
                return;
            }
            if (fdr.State == States.STATE_INHIBITED)
            {
                MMI.UnInhibit(fdr);
            }
            else
            {
                MMI.Inhibit(fdr);
            }
            ReloadAfterAction(fdr);
        }

        private void CnlClicked()
        {
            FDP2.FDR fdr = _fdr;
            if (fdr == null)
            {
                return;
            }
            if (!fdr.HavePermission && fdr.IsTracked)
            {
                ShowError(new FDRParseException(FDRParseException.CONT_FP_EXISTS, fdr.Callsign));
                return;
            }

            string message = fdr.State <= States.STATE_COORDINATED
                ? "This will deactivate the FDR for " + fdr.Callsign + "."
                : "This will finish the FDR for " + fdr.Callsign + ".";
            if (Internals.Confirm("Cancel " + fdr.Callsign + "?", message))
            {
                FDP2.CancelFDR(fdr, sendEst: true);
            }
            ReloadAfterAction(fdr);
        }

        private void AccClicked()
        {
            if (_fdr != null)
            {
                MMI.AcceptJurisdiction(_fdr);
            }
        }

        private void HndClicked()
        {
            if (_fdr != null)
            {
                MMI.OpenHandoffMenu(_fdr);
            }
        }

        private void PrlClicked()
        {
            FDP2.FDR fdr = _fdr;
            if (fdr == null)
            {
                return;
            }
            if (fdr.CoupledTrack != null)
            {
                ShowError("PRL not available (track coupled)");
                return;
            }
            ClearError();
            MMI.OpenPRLMenu(fdr, Cursor.Position);
        }

        private void CflClicked()
        {
            FDP2.FDR fdr = _fdr;
            if (fdr == null)
            {
                return;
            }
            Track track = MMI.FindTrack(fdr);
            if (track == null)
            {
                ShowError("CFL not available (no track)");
                return;
            }
            ClearError();
            MMI.OpenCFLMenu(track, Cursor.Position);
        }

        /// <summary>NCOU: break the radar coupling and stop auto-coupling.</summary>
        private void NcouClicked()
        {
            FDP2.FDR fdr = _fdr;
            if (fdr?.CoupledTrack == null)
            {
                return;
            }
            RDP.DeCouple(fdr.CoupledTrack);
            fdr.InhibitAutoCouple = true;
            RefreshState();
        }

        /// <summary>BCOU: couple to the selected radar track, or hand coupling back to the system.</summary>
        private void BcouClicked()
        {
            FDP2.FDR fdr = _fdr;
            if (fdr == null || fdr.CoupledTrack != null)
            {
                return;
            }

            Track selected = MMI.SelectedTrack;
            if (selected != null && selected.Type == Track.TrackTypes.TRACK_TYPE_RADAR && selected.GetFDR() == null)
            {
                RDP.Couple(fdr, selected.GetRadarTrack());
                fdr.InhibitAutoCouple = true;
                MMI.DeselectTrack();
            }
            else if (fdr.InhibitAutoCouple)
            {
                fdr.InhibitAutoCouple = false;
            }
            else
            {
                ShowError("Invalid / no radar track selected");
            }
            RefreshState();
        }

        private void ToggleGraphicRoute()
        {
            if (_routeTrack != null)
            {
                HideGraphicRoute();
                return;
            }

            if (_fdr == null)
            {
                return;
            }
            Track track = MMI.FindTrack(_fdr);
            if (track == null)
            {
                ShowError("No track to display route for");
                return;
            }
            ClearError();
            MMI.ShowGraphicRoute(track);
            _routeTrack = track;
        }

        private void HideGraphicRoute()
        {
            if (_routeTrack == null)
            {
                return;
            }
            try
            {
                MMI.HideGraphicRoute(_routeTrack);
            }
            catch
            {
            }
            _routeTrack = null;
        }

        private void ReloadAfterAction(FDP2.FDR fdr)
        {
            if (_fdr == fdr && _mode == Mode.View)
            {
                LoadFieldData(true);
                StampUpdateTime();
                RefreshState();
            }
        }

        // =====================================================================
        //  Messages
        // =====================================================================

        /// <summary>Runs a UI action; any failure is shown in the message area instead of crashing vatSys.</summary>
        private void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (FDRParseException ex)
            {
                ShowError(ex);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
                Plugin.Report(ex);
                try
                {
                    RefreshState();
                }
                catch
                {
                }
            }
        }

        private void ShowError(FDRParseException ex)
        {
            ShowError(string.IsNullOrEmpty(ex.OffendingElement) ? ex.Cause : "'" + ex.OffendingElement + "' : " + ex.Cause);
        }

        private void ShowError(string message)
        {
            Color warning = EcTheme.Warning;
            errorPanel.BackColor = warning;
            errorLabel.BackColor = warning;
            errorLabel.Text = message;
            errorLabel.Visible = true;
            acidField.ErrorMode(true);
            FlushPaint();
        }

        private void ClearError()
        {
            if (!errorLabel.Visible)
            {
                return;
            }
            errorLabel.Visible = false;
            errorLabel.Text = "";
            errorLabel.BackColor = EcTheme.Background;
            errorPanel.BackColor = EcTheme.Background;
            acidField.ErrorMode(false);
            FlushPaint();
        }

        // =====================================================================
        //  Teardown
        // =====================================================================

        protected override void OnClosing(CancelEventArgs e)
        {
            Unsubscribe();
            CancelPendingRequest();
            HideGraphicRoute();
            FDP2.FDRsChanged -= FDP2_FDRsChanged;
            Network.ValidATCChanged -= Network_ValidATCChanged;
            MMI.SelectedTrackChanged -= MMI_SelectedTrackChanged;
            base.OnClosing(e);
        }
    }
}
