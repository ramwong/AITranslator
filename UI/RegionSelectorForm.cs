using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AITranslator.Native;

namespace AITranslator.UI;

/// <summary>框選流程的結束狀態（<see cref="RegionSelectionResult.Status"/>）。</summary>
public enum RegionSelectionStatus
{
    /// <summary>使用者放開左鍵完成一個不小於最小尺寸的矩形。</summary>
    Selected,

    /// <summary>使用者以 ESC / 右鍵 / 熱鍵重入（<see cref="RegionSelectorForm.CancelSelection"/>）取消。</summary>
    Cancelled,

    /// <summary>放開左鍵時矩形小於最小尺寸（<see cref="RegionSelectorForm.MinRectSize"/>）。</summary>
    TooSmall,
}

/// <summary>
/// <see cref="RegionSelectorForm.SelectRegion"/> 的結果。
/// <see cref="Rect"/> 為<b>虛擬螢幕實體像素座標</b>（直接餵給
/// <see cref="ScreenCaptureService"/> 的擷取，跨螢幕拖出的矩形也不失真）；
/// <see cref="Status"/> 為 <see cref="RegionSelectionStatus.Cancelled"/> 時
/// <see cref="Rect"/> 為 <see cref="System.Drawing.Rectangle.Empty"/>。
/// </summary>
/// <param name="Rect">選取矩形（虛擬螢幕實體像素座標）。</param>
/// <param name="Status">結束狀態。</param>
public sealed record RegionSelectionResult(Rectangle Rect, RegionSelectionStatus Status);

/// <summary>
/// 全螢幕框選 overlay（OCR 螢幕框選模式的第一步）：
/// 為 <see cref="Screen.AllScreens"/> 每台螢幕蓋一張暗幕（滑鼠所在螢幕為<b>主</b> overlay，
/// 其餘為同款<b>輔</b> overlay——純暗幕、不接受拖曳），使用者以十字游標拉一個矩形，
/// 框內以擷取於暗幕出現<b>前</b>的桌面原圖「亮起」並加白色 1px 邊框；放開左鍵或取消後
/// 關閉所有 overlay，<see cref="SelectRegion"/> 回傳矩形與結束狀態。
///
/// <para><b>座標系統（DPI）</b>：全流程使用<b>實體像素</b>座標。專案以
/// <c>HighDpiMode.PerMonitorV2</c> 執行（見 Program.cs），此時 <see cref="Screen.Bounds"/>、
/// <see cref="Cursor.Position"/>、<see cref="Control.PointToScreen(Point)"/> 都回傳實體像素，
/// 表單的 Bounds（以 <see cref="Screen.Bounds"/> 指定）與其 client 座標即實體像素，
/// 不需任何 DPI 換算；<see cref="Normalize(Point, Point)"/> 的兩個 Point 參數
/// 亦為虛擬螢幕實體像素座標。</para>
///
/// <para><b>繪製</b>：每張 overlay 於 OnPaint 先把桌面原圖（<see cref="SelectRegion"/> 開場、
/// 任何暗幕出現前以 <see cref="Graphics.CopyFromScreen(int, int, int, int, Size)"/> 拍下的
/// 虛擬螢幕快照）1:1 畫為底層，再以 <see cref="System.Drawing.Region"/> 排除選取矩形後
/// 疊 alpha 102 的黑（0.4 黑）當暗幕，最後沿選區畫白色 1px 邊框。刻意<b>不用</b>
/// <see cref="Form.Opacity"/> 建立 layered window：per-pixel alpha 表單的洞內再畫
/// CopyFromScreen 原圖會「暗幕 × 0.4」double-dim，邊框也會被壓成半透明，
/// 與「選區內亮起原畫面＋實白邊框」互斥；同時快照必須在暗幕出現前拍——
/// 繪製當下再 CopyFromScreen 只會把自己的暗幕抓進來（BitBlt 擷的是含本 overlay 的合成結果）。</para>
///
/// <para><b>輸入</b>：拖曳由<b>滑鼠所在螢幕</b>的主 overlay 處理（MouseDown 記起點並
/// <see cref="Control.Capture"/>——控制項捕捉讓拖出本螢幕的 MouseMove/MouseUp 仍送回本表單，
/// 座標以虛擬螢幕系表達，跨螢幕矩形不失真）；其餘螢幕的輔 overlay 純暗幕、不接受拖曳。
/// ESC 與<b>滑鼠右鍵</b>的取消走低層級（LL）hook 的「觀察型」攔截：overlay 以
/// <c>WS_EX_NOACTIVATE</c> + <see cref="Form.ShowWithoutActivation"/> 刻意不 取得
/// 鍵盤焦點（反目標：造成使用者應用程式的輸入焦點遺失），表單本身收不到 KeyDown，
/// 故沿用 <see cref="DismissHook"/> 的不變量——hook proc 的每個分支都以
/// <see cref="Win32.CallNextHookEx"/> 回傳值結束、絕不吞輸入，且右鍵 hook 覆蓋
/// 所有螢幕（輔 overlay 被 modal loop 停用，表單訊息不可靠）。兩條 hook 的安裝
/// 結果逐條檢查（<see cref="InstallHooks"/>）；安裝失敗為退化路徑——僅該條取消
/// 途徑不可用，框選與結束流程不變（詳見 InstallHooks 註解）。</para>
///
/// <para><b>結束途徑與結果</b>：放開左鍵 → <see cref="Decide"/>（&lt; 8px → TooSmall，否則
/// Selected）；ESC / 右鍵 / 熱鍵重入（<see cref="CancelSelection"/>）→ Cancelled。
/// 結束動作（<see cref="FinishSelection"/>）冪等：先到者定案，重複觸發不改結果。</para>
///
/// <para><b>訊息迴圈與單次使用</b>：<see cref="SelectRegion"/> 以 <see cref="Form.ShowDialog()"/>
/// 進 modal 訊息迴圈（期間後到熱鍵仍會送達 <c>MessageWindow</c> 的 <c>WM_HOTKEY</c>——
/// <see cref="CancelSelection"/> 即為熱鍵重入取消而設計，並對
/// <see cref="Control.InvokeRequired"/> 安全）。選取結束時（正常或例外路徑）會
/// 解除 hook、關閉並 Dispose 輔 overlay、釋放快照，並 Dispose 本表單——每個實例
/// 只能呼叫一次 <see cref="SelectRegion"/>，需再次框選請新建實例。</para>
/// </summary>
public sealed class RegionSelectorForm : Form
{
    /// <summary>框選矩形允許的最小寬/高（px）；小於此值放開滑鼠即為 TooSmall。</summary>
    public const int MinRectSize = 8;

    /// <summary>暗幕顏色：alpha 102 的黑（102/255 ≈ 0.4，即 brief 的「暗幕 0.4 黑」；見類別註解的繪製說明）。</summary>
    private static readonly Color CurtainColor = Color.FromArgb(102, 0, 0, 0);

    /// <summary>選取矩形邊框顏色（白色 1px）。</summary>
    private static readonly Color FrameColor = Color.White;

    /// <summary>本表單所屬的選取 session（主 overlay 建立；輔 overlay 導向同一份）。null = 尚未開始或已結束。</summary>
    private SelectionSession? _session;

    /// <summary>本表單是否為 session 的主 overlay（滑鼠所在螢幕那張；拖曳只由它處理，見類別註解）。</summary>
    private bool IsPrimary => _session?.Primary == this;

    /// <summary>是否有左鍵拖曳進行中（MouseDown → MouseUp 之間為 true）。</summary>
    private bool _dragging;

    /// <summary>拖曳起點（虛擬螢幕實體像素座標）。</summary>
    private Point _dragStartVirtual;

    /// <summary>結束時的矩形（虛擬螢幕實體像素座標；Cancelled 為 Empty）——由 FinishSelection 定案後供 SelectRegion 回傳。</summary>
    private Rectangle _resultRect;

    /// <summary>結束狀態；預設 Cancelled（意外關閉路徑的保守退路）。</summary>
    private RegionSelectionStatus _resultStatus = RegionSelectionStatus.Cancelled;

    // ---- LL hook（ESC / 右鍵取消；僅主 overlay 於 SelectRegion 期間安裝）----

    /// <summary>WH_KEYBOARD_LL hook handle（觀察 Esc 鍵）；IntPtr.Zero = 未安裝。</summary>
    private IntPtr _keyboardHook;

    /// <summary>WH_MOUSE_LL hook handle（觀察右鍵按下）；IntPtr.Zero = 未安裝。</summary>
    private IntPtr _mouseHook;

    // 保持 delegate 參考以防 GC 回收已封送的 hook 函式指標（沿用 DismissHook 的做法）。
    private Win32.LowLevelKeyboardProc? _keyboardProc;
    private Win32.LowLevelMouseProc? _mouseProc;

    /// <summary>建立空白背景的 overlay 表單；實際位置/大小由 <see cref="SelectRegion"/> 指定。</summary>
    public RegionSelectorForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;

        // 拖曳中每次 MouseMove 都會 Invalidate（髒區為新舊矩形聯集），
        // DoubleBuffer 讓 OnPaint 在背景緩衝完成後一次貼上，避免暗幕/原圖閃爍。
        DoubleBuffered = true;
    }

    /// <summary>
    /// 疊加擴充樣式：不啟動（框選期間不搶走使用者目前視窗的鍵盤焦點）、
    /// 工具視窗（不出現在 Alt+Tab）、置頂（蓋過使用者的所有視窗）。
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 // WS_EX_NOACTIVATE
                        | 0x00000080 // WS_EX_TOOLWINDOW
                        | 0x00000008; // WS_EX_TOPMOST
            return cp;
        }
    }

    /// <summary>顯示時不啟動（不搶走使用者目前視窗的焦點）。</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// 純函式（供單元測試）：兩點 → 規範化矩形（左上角為兩點最小座標，
    /// 寬/高為兩點座標差的絕對值、≥ 0）。座標為<b>虛擬螢幕實體像素</b>（見類別註解）。
    /// </summary>
    public static Rectangle Normalize(Point start, Point end) => new(
        Math.Min(start.X, end.X),
        Math.Min(start.Y, end.Y),
        Math.Abs(start.X - end.X),
        Math.Abs(start.Y - end.Y));

    /// <summary>
    /// 純函式（供單元測試）：依矩形寬/高決定結束狀態——
    /// 寬或高小於 <see cref="MinRectSize"/> → <see cref="RegionSelectionStatus.TooSmall"/>；
    /// 否則 <see cref="RegionSelectionStatus.Selected"/>。
    /// </summary>
    public static RegionSelectionStatus Decide(Rectangle rect)
        => rect.Width < MinRectSize || rect.Height < MinRectSize
            ? RegionSelectionStatus.TooSmall
            : RegionSelectionStatus.Selected;

    /// <summary>
    /// 進入框選模式並回傳結果：
    /// 先拍下虛擬螢幕原圖（暗幕出現前）、為每台螢幕建立 overlay 並 <see cref="Form.Show"/>、
    /// 安裝 ESC/右鍵 LL hook 後以 <see cref="Form.ShowDialog()"/> 進 modal 訊息迴圈，
    /// 到結束途徑（放開左鍵 / ESC / 右鍵 / 外部 <see cref="CancelSelection"/>）關閉所有 overlay 後返回。
    /// 必須在 UI 執行緒呼叫；每個實例只能使用一次（結束時會 Dispose 本表單，見類別註解）。
    /// </summary>
    public RegionSelectionResult SelectRegion()
    {
        // 單次使用契約：重入或於已 Dispose 的實例上呼叫都是呼叫端錯誤（Task 5 每次新建實例）。
        if (_session != null)
        {
            throw new InvalidOperationException("SelectRegion 每個實例只能使用一次；請新建 RegionSelectorForm。");
        }

        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(RegionSelectorForm));
        }

        var interactiveScreen = Screen.FromPoint(Cursor.Position); // 熱鍵按下時滑鼠所在螢幕 → 主 overlay
        var virtualBounds = ComputeVirtualBounds();
        _resultRect = Rectangle.Empty;
        _resultStatus = RegionSelectionStatus.Cancelled;
        _session = new SelectionSession(virtualBounds, TryCaptureVirtualScreen(virtualBounds));

        try
        {
            var primary = this;
            _session.Primary = primary;

            // 每台螢幕一張 overlay：滑鼠所在螢幕用本表單，其餘建立輔 overlay。
            // StartPosition=Manual + 以 Screen.Bounds（實體像素）指定 Bounds，
            // 於 Show（建立 handle）「前」指定 → 一次建到定位，不發生跨 Dpi 移動
            // （跨螢幕移動才會觸發 WM_DPICHANGED 的重縮放，蓋不滿各自螢幕）。
            foreach (var screen in Screen.AllScreens)
            {
                RegionSelectorForm overlay;
                if (screen.Bounds == interactiveScreen.Bounds)
                {
                    overlay = primary;
                }
                else
                {
                    overlay = new RegionSelectorForm();
                    _session.Shadows.Add(overlay);
                }

                overlay._session = _session;
                overlay.Bounds = screen.Bounds;

                // 輔 overlay 先 Show（modal loop 之前暗幕已鋪滿其餘螢幕）；
                // 主 overlay 不可（modal 的表單不能先 Visible → ShowDialog 會擲
                //InvalidOperationException），其顯示由下行 ShowDialog 完成。
                if (overlay != primary)
                {
                    overlay.Show(); // WS_EX_NOACTIVATE + ShowWithoutActivation：不取得焦點
                }
            }

            InstallHooks(); // ESC / 右鍵取消（見類別註解：無啟動表單收不到鍵盤輸入，走 LL hook）
            ShowDialog(); // modal 訊息迴圈到所有 overlay 關閉（結束途徑見 FinishSelection）
        }
        finally
        {
            UninstallHooks();

            var session = _session;
            _session = null;
            if (session != null)
            {
                foreach (var shadow in session.Shadows)
                {
                    shadow.Dispose();
                }

                session.Snapshot?.Dispose();
            }

            // 單次使用：本表單至此沒有任何用處；Task 5 只在 SelectRegion 期間（尚未返回）持有
            // 參考以便重入取消，正常與例外路徑都不會有殘留的視窗 handle。
            Dispose();
        }

        // _resultStatus 初始值即 Cancelled：FinishSelection 未及定案的意外關閉路徑（如系統關機）
        // 也回傳一致的 Cancelled，不會回「無狀態」的結果。
        return new RegionSelectionResult(_resultRect, _resultStatus);
    }

    /// <summary>
    /// 從外部取消框選（等同 ESC：所有 overlay 關閉、<see cref="SelectRegion"/> 回
    /// <see cref="RegionSelectionStatus.Cancelled"/>）。供 AppController 於熱鍵重入時呼叫。
    /// 冪等：選取已結束（或尚未開始）時呼叫為無動作；並對
    /// <see cref="Control.InvokeRequired"/> 安全——非本表單執行緒的呼叫以
    /// <see cref="Control.BeginInvoke(Delegate)"/> 封送回後關閉（modal 迴圈會幫浦到佇列）。
    /// </summary>
    public void CancelSelection()
    {
        if (InvokeRequired)
        {
            // 非 UI 執行緒：封送回表單執行緒才動 UI。此時尚未開始／已結束／已釋放
            // （無 handle 可封送）都視為無動作——FinishSelection 的冪等防護亦於此重覆把關。
            if (!IsDisposed && IsHandleCreated)
            {
                _ = BeginInvoke(new MethodInvoker(CancelSelection));
            }

            return;
        }

        FinishSelection(Rectangle.Empty, RegionSelectionStatus.Cancelled);
    }

    /// <summary>把整個選取流程定案並收尾：記下結果後關閉所有 overlay（主 overlay 的 Close 讓 ShowDialog 返回）。冪等。</summary>
    private void FinishSelection(Rectangle rect, RegionSelectionStatus status)
    {
        var session = _session;
        if (session == null || session.Ended)
        {
            return; // 已結束（或尚未開始）：重入的結束事件（hook / CancelSelection / 拖放同時觸發）不改結果
        }

        session.Ended = true;
        _resultRect = rect;
        _resultStatus = status;
        _dragging = false;
        Capture = false;

        CloseAllOverlays();
    }

    /// <summary>關閉所有 overlay（必須在表單執行緒上）：輔 overlay 先關、主 overlay 最後，暗幕在 ShowDialog 返回前已全數撤下。</summary>
    private void CloseAllOverlays()
    {
        var session = _session;
        if (session == null)
        {
            return;
        }

        foreach (var shadow in session.Shadows)
        {
            if (!shadow.IsDisposed && shadow.IsHandleCreated)
            {
                shadow.Close();
            }
        }

        if (!IsDisposed && IsHandleCreated)
        {
            Close(); // 主 overlay：Close 讓以 ShowDialog 顯示的本表單結束 modal 迴圈
        }
    }

    // ---- 左鍵拖曳（只有滑鼠所在螢幕的主 overlay 收得到拖曳）----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        // 僅左鍵開始拖曳；拖曳只由主 overlay 處理（brief）——輔 overlay 雖然也被 modal
        // loop 停用而不會收到真實滑鼠訊息，仍以 IsPrimary 明示此契約。
        // session 尚未開始／已結束（hook 已取消、表單關閉中）的晚到事件亦忽略。
        if (e.Button != MouseButtons.Left || _session is not { Ended: false } || !IsPrimary)
        {
            return;
        }

        _dragging = true;
        Capture = true; // 明確捕捉：拖到本螢幕之外（跨越其他螢幕的輔 overlay）仍由本表單收 MouseMove/MouseUp
        _dragStartVirtual = PointToScreen(e.Location); // client → 虛擬螢幕實體像素座標
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || !IsPrimary || _session is not { Ended: false })
        {
            return;
        }

        // 先記舊矩形再更新（兩張 overlay 都用「新舊矩形聯集」做髒區重繪，避免整面重畫）。
        var oldVirtual = _session.RectVirtual;
        _session.RectVirtual = Normalize(_dragStartVirtual, PointToScreen(e.Location));
        var newVirtual = _session.RectVirtual;

        // 失效區＝新舊矩形聯集，外加 2px 邊距（筆跡/GDI 邊界像素可能落在矩形
        // 涵蓋範圍外緣一至兩格；保險把邊界也納入，僅多刷細邊條，不整面重繪）。
        var dirty = Rectangle.Union(ToClient(oldVirtual), ToClient(newVirtual));
        dirty.Inflate(2, 2);
        Invalidate(dirty);
        foreach (var shadow in _session.Shadows)
        {
            // 選區跨到的螢幕，其輔 overlay 也同步把該部分挖亮（框在各螢幕上位置一致、無位移）。
            var shadowDirty = Rectangle.Union(shadow.ToClient(oldVirtual), shadow.ToClient(newVirtual));
            shadowDirty.Inflate(2, 2);
            shadow.Invalidate(shadowDirty);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragging = false;
        Capture = false;

        if (_session is not { Ended: false })
        {
            return; // hook 已先行取消（session 結束中）：結果以取消為準
        }

        // 放開左鍵即結束（不提供再次拉框重選）：用「起點 + 放開時的點」計算最終矩形
        // （與最後一次 MouseMove 的差異以放開當下為準），尺寸不足即 TooSmall。
        var rect = Normalize(_dragStartVirtual, PointToScreen(e.Location));
        FinishSelection(rect, Decide(rect));
    }

    // ---- 繪製（見類別註解：快照為底 + Region 排除選區填半透明黑 + 白 1px 邊框）----

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var session = _session;
        if (session == null)
        {
            return; // 已結束（表單關閉中）的殘餘 Paint：保留 BackColor 的黑底即可
        }

        var g = e.Graphics;
        var client = ClientRectangle;
        var snapshot = session.Snapshot;

        if (snapshot == null)
        {
            // 快照失敗的安全網：整面維持黑底，流程（拖曳/取消/回傳）仍正常，只是沒有「亮起」；
            // 暗幕與邊框照畫（使用者仍看得到框與暗幕的反差）。
            g.Clear(Color.Black);
        }
        else
        {
            // 1) 桌面原圖為底：本表單覆蓋範圍（client）對映到快照中的區域，1:1 貼上。
            //    （快照在所有 overlay Show 之前以 CopyFromScreen 拍下；繪製當下再抓只會抓到自己的暗幕。）
            var virtualArea = ToVirtual(client);
            var sourceArea = new Rectangle(
                virtualArea.X - session.VirtualBounds.X,
                virtualArea.Y - session.VirtualBounds.Y,
                virtualArea.Width,
                virtualArea.Height);
            g.DrawImage(snapshot, client, sourceArea, GraphicsUnit.Pixel);
        }

        // 2) 暗幕：排除選取矩形後填 alpha 102 黑（0.4 黑）；選區之內保留底層的原圖（亮起）。
        var selectionClient = ToClient(session.RectVirtual);
        using (var curtainArea = new Region(client))
        {
            if (!selectionClient.IsEmpty)
            {
                curtainArea.Exclude(selectionClient);
            }

            using var curtain = new SolidBrush(CurtainColor);
            g.FillRegion(curtain, curtainArea);
        }

        // 3) 白色 1px 邊框——畫在選區「內」的最後一列/行像素上：
        //    GDI+ DrawRectangle 把線畫在邊界座標（X+Width / Y+Height）上，比矩形
        //    實際涵蓋的最後一個像素行/列再往外一格；因此以 (Width-1, Height-1)
        //    指定幾何，讓框完全落在選區像素內（框外不會多出 1px 白線），
        //    也讓 OnMouseMove 的「新舊矩形聯集」失效重繪能完整蓋掉縮小方向的殘框
        //    （邊界像素若超出矩形涵蓋範圍，聯集失效區永遠蓋不到——實測即為縮小時殘框的根因）。
        if (!selectionClient.IsEmpty)
        {
            var frameRect = new Rectangle(
                selectionClient.X,
                selectionClient.Y,
                selectionClient.Width - 1,
                selectionClient.Height - 1);
            using var frame = new Pen(FrameColor) { Alignment = PenAlignment.Inset };
            g.DrawRectangle(frame, frameRect);
        }
    }

    /// <summary>client 座標矩形 → 虛擬螢幕實體像素座標（平移不縮放；兩者同為實體像素，見類別註解）。</summary>
    private Rectangle ToVirtual(Rectangle rectClient)
        => new(PointToScreen(rectClient.Location), rectClient.Size);

    /// <summary>虛擬螢幕實體像素座標矩形 → 本表單 client 座標（平移不縮放）。</summary>
    private Rectangle ToClient(Rectangle rectVirtual)
        => new(PointToClient(rectVirtual.Location), rectVirtual.Size);

    /// <summary>所有螢幕 Bounds 的聯集（最小外接矩形）= 虛擬螢幕範圍（實體像素）。</summary>
    private static Rectangle ComputeVirtualBounds()
    {
        var screens = Screen.AllScreens; // 至少一台螢幕
        var virtualBounds = screens[0].Bounds;

        for (var i = 1; i < screens.Length; i++)
        {
            virtualBounds = Rectangle.Union(virtualBounds, screens[i].Bounds);
        }

        return virtualBounds;
    }

    /// <summary>
    /// 拍下虛擬螢幕原圖（暗幕出現<b>前</b>呼叫，見類別註解「繪製」）。
    /// 失敗（如極端的多螢幕組態下 BitBlt 拒絕）不中斷框選：回傳 null，
    /// overlay 退化為整面黑底（OnPaint 安全網），並記錄到主控台以利診斷。
    /// </summary>
    private static Bitmap? TryCaptureVirtualScreen(Rectangle bounds)
    {
        try
        {
            var snapshot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(snapshot))
            {
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AITranslator: 框選原畫面快照失敗：{ex}");
            return null;
        }
    }

    // ---- ESC / 右鍵取消的 LL hook（觀察型，不變量沿用 DismissHook：絕不吞輸入、例外不外洩）----

    /// <summary>
    /// 安裝 ESC（WH_KEYBOARD_LL）與右鍵按下（WH_MOUSE_LL）兩條觀察型 LL hook。
    /// 安裝結果逐條檢查：SetWindowsHookExW 失敗（回傳 IntPtr.Zero）時記錄到主控台並繼續
    /// （不讓框選整段中斷）。退化路徑＝該條取消途徑不可用，其餘不變——
    /// <list type="bullet">
    /// <item>ESC hook 失敗：ESC 取消不可用（overlay 不啟動、收不到 KeyDown，LL hook 是唯一 ESC 途徑），</item>
    /// <item>右鍵 hook 失敗：右鍵取消不可用（含被 modal loop 停用的輔 overlay 螢幕），</item>
    /// <item>兩者皆失敗：只剩「放開左鍵」可結束（結果為 Selected/TooSmall；Cancelled 僅可由
    ///  外部 <see cref="CancelSelection"/>——熱鍵重入——達成，且選取終究可正常完成/回傳）。</item>
    /// </list>
    /// 一般環境下 LL hook 安裝不會失敗（DismissHook 亦同）。
    /// </summary>
    private void InstallHooks()
    {
        _keyboardProc = EscapeKeyboardHookProc;
        _keyboardHook = Win32.SetWindowsHookExW(Win32.WH_KEYBOARD_LL, _keyboardProc, IntPtr.Zero, 0);
        _mouseProc = RightButtonMouseHookProc;
        _mouseHook = Win32.SetWindowsHookExW(Win32.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);

        if (_keyboardHook == IntPtr.Zero)
        {
            Console.Error.WriteLine("AITranslator: 框選 ESC 取消掛鉤安裝失敗（WH_KEYBOARD_LL）——框選期間 ESC 取消不可用。");
        }

        if (_mouseHook == IntPtr.Zero)
        {
            Console.Error.WriteLine("AITranslator: 框選右鍵取消掛鉤安裝失敗（WH_MOUSE_LL）——框選期間右鍵取消不可用。");
        }
    }

    /// <summary>解除兩條 LL hook 並釋放 delegate 參考（SelectRegion 的 finally 必經；未裝時呼叫為無動作）。</summary>
    private void UninstallHooks()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        // 已 Unhook，Windows 不會再呼叫，可安全釋放封送的 delegate。
        _keyboardProc = null;
        _mouseProc = null;
    }

    /// <summary>鍵盤 LL hook：觀察 Esc 鍵 → 取消框選；一律放行 hook 鏈。</summary>
    private IntPtr EscapeKeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _session is { Ended: false })
        {
            var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            if (info.vkCode == Win32.VK_ESCAPE)
            {
                SafeFinishCancelled();
            }
        }

        // 不吞輸入：Esc 照常送給當下應用程式，本 hook 只負責觀察。
        return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <summary>滑鼠 LL hook：觀察右鍵按下 → 取消框選（輔 overlay 被 modal loop 停用，經此 hook 覆蓋所有螢幕）；一律放行 hook 鏈。</summary>
    private IntPtr RightButtonMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _session is { Ended: false }
            && wParam.ToInt32() == Win32.WM_RBUTTONDOWN)
        {
            SafeFinishCancelled();
        }

        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 在 hook proc 內定案「取消」：FinishSelection 不可拋例外（例外逃出 LL hook proc
    /// 可能造成全系統輸入死鎖或程序崩潰，與 DismissHook 同理），故包 try/catch 記主控台。
    /// </summary>
    private void SafeFinishCancelled()
    {
        try
        {
            FinishSelection(Rectangle.Empty, RegionSelectionStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AITranslator: 取消框選失敗：{ex}");
        }
    }

    /// <summary>
    /// 一次框選的共用狀態（主 overlay 建立，輔 overlay 導向同一份）：
    /// 虛擬螢幕範圍、暗幕出現前的桌面快照、輔 overlay 清單與拖曳中的矩形
    /// （兩張 overlay 都以虛擬螢幕物理座標對照快照繪製，框跨螢幕也一致）。
    /// </summary>
    private sealed class SelectionSession
    {
        /// <summary>虛擬螢幕範圍（實體像素）＝ 所有螢幕 Bounds 的聯集（快照座標系原點）。</summary>
        public readonly Rectangle VirtualBounds;

        /// <summary>暗幕出現前的桌面原圖（快照座標系之 (0,0) 即 VirtualBounds 左上角）；擷取失敗為 null（退化黑底）。</summary>
        public readonly Bitmap? Snapshot;

        /// <summary>處理拖曳的主 overlay（滑鼠所在螢幕那張）；由 SelectRegion 指定。</summary>
        public RegionSelectorForm Primary = null!;

        /// <summary>其餘螢幕的輔 overlay（純暗幕、不接受拖曳）。</summary>
        public readonly List<RegionSelectorForm> Shadows = new();

        /// <summary>拖曳中的矩形（虛擬螢幕實體像素座標）；Empty = 尚未拖曳。</summary>
        public Rectangle RectVirtual;

        /// <summary>選取流程已結束（FinishSelection 冪等防護）。</summary>
        public bool Ended;

        public SelectionSession(Rectangle virtualBounds, Bitmap? snapshot)
        {
            VirtualBounds = virtualBounds;
            Snapshot = snapshot;
        }
    }
}