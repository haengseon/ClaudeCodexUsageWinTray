// ClaudeCodexUsageWinTray
// build: csc /nologo /target:winexe /optimize /win32icon:assets\icon.ico /resource:assets\banner.png,banner.png /r:System.Web.Extensions.dll /out:ClaudeCodexUsageWinTray.exe ClaudeCodexUsageWinTray.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("ClaudeCodexUsageWinTray")]
[assembly: AssemblyProduct("ClaudeCodexUsageWinTray")]
[assembly: AssemblyVersion("0.2.0.0")]

class Win
{
    public double Pct = -1;          // -1 = 데이터 없음
    public DateTimeOffset? Reset;
    public string Label;
    public Win(string label) { Label = label; }
    public double Now { get { return Reset.HasValue && Reset.Value < DateTimeOffset.Now ? 0 : Pct; } }
}

// Login = 로그인 정보 없음(설치 안 했거나 로그인 안 함), Limited = 요청이 많아 잠시 쉬는 중
enum State { Checking, Ok, Login, Relogin, Error, Limited }

// 로그인하는 방법: CLI 명령 / 데스크톱 앱 열기 / 설치 페이지 열기
enum Setup { None, Cli, App }

class Svc
{
    public string Name, Cli, LoginArgs, AppScheme, AltScheme, Download; public Color Brand;
    public List<Win> Wins = new List<Win>();
    public string Error, Plan; public DateTime Updated, Checked, NextAt;
    public State State;
    public int Backoff;   // 429를 받으면 늘어나는 조회 간격(분)
    public Setup How; public string HowTarget;   // 로그인 방법과 그 대상(CLI 경로나 앱 주소)
    public bool Shown { get { return State != State.Login; } }   // 로그인 정보가 있는 서비스만 화면에 그린다
    public Win Short { get { var w = Wins; return w.Count > 0 ? w[0] : null; } }   // 갱신 스레드가 목록을 바꿔치기해도 안전하게
    public Win Week { get { var w = Wins; return w.Count > 1 ? w[1] : null; } }
}

// 로그인이 필요할 때. Missing = 로그인 정보 없음, 아니면 인증 만료
class AuthError : Exception
{
    public readonly bool Missing;
    public AuthError(string message, bool missing) : base(message) { Missing = missing; }
}

static class Api
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    // CLI·데스크톱 앱과 같은 곳을 본다 (CLAUDE_CONFIG_DIR / CODEX_HOME 을 바꿔 쓰는 경우 포함)
    public static readonly string ClaudeCred = Path.Combine(Dir("CLAUDE_CONFIG_DIR", ".claude"), ".credentials.json");
    public static readonly string CodexAuth = Path.Combine(Dir("CODEX_HOME", ".codex"), "auth.json");

    static string Dir(string env, string name)
    {
        var v = Environment.GetEnvironmentVariable(env);
        return string.IsNullOrEmpty(v) ? Path.Combine(Home, name) : v;
    }

    static Dictionary<string, object> D(object o) { return o as Dictionary<string, object>; }
    static double Num(object o) { return o == null ? -1 : Convert.ToDouble(o); }
    static Dictionary<string, object> Read(string path) { return File.Exists(path) ? D(Json.DeserializeObject(File.ReadAllText(path))) : null; }
    static Dictionary<string, object> Part(Dictionary<string, object> root, string key) { return root != null && root.ContainsKey(key) ? D(root[key]) : null; }
    static AuthError Expired(string name) { return new AuthError(L.T(name + " 인증이 만료되었습니다. 다시 로그인해 주세요.", name + " sign-in has expired. Please sign in again."), false); }

    // 네트워크 없이 로그인 정보가 있는지만 본다 (시작할 때 화면 구성용)
    public static bool HasLogin(string path, string key)
    {
        try { return Part(Read(path), key) != null; } catch { return File.Exists(path); }
    }

    static string Http(string method, string url, Dictionary<string, string> headers, string body)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = method; req.Timeout = 15000; req.UserAgent = "ClaudeCodexUsageWinTray";
        req.Accept = "application/json";
        if (headers != null) foreach (var h in headers) req.Headers[h.Key] = h.Value;
        if (body != null)
        {
            req.ContentType = "application/json";
            var b = Encoding.UTF8.GetBytes(body);
            using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
        }
        using (var res = req.GetResponse())
        using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
    }

    public static int Status(WebException e)
    {
        var r = e.Response as HttpWebResponse;
        return r == null ? 0 : (int)r.StatusCode;
    }

    // 토큰을 갱신하고도 401이면 다시 로그인해야 한다
    static string AfterRefresh(Func<string> call, string name)
    {
        try { return call(); }
        catch (WebException e) { if (Status(e) == 401) throw Expired(name); throw; }
    }

    // CLI·앱과 같은 파일을 쓰므로 임시 파일에 쓴 뒤 한 번에 교체한다 (잠겨 있으면 잠깐 기다렸다 다시)
    static void SaveJson(string path, Dictionary<string, object> root)
    {
        var tmp = path + ".ccuwt.tmp";
        File.WriteAllText(tmp, Json.Serialize(root), new UTF8Encoding(false));
        for (int i = 0; ; i++)
        {
            try { File.Replace(tmp, path, null); return; }
            catch (IOException) { if (i >= 9) { File.Delete(tmp); throw; } Thread.Sleep(100); }
        }
    }

    // ---------------- Claude ----------------
    const string ClaudeClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    static readonly string[] ClaudeTokenUrls = { "https://platform.claude.com/v1/oauth/token", "https://console.anthropic.com/v1/oauth/token" };

    public static void Claude(Svc s)
    {
        var root = Read(ClaudeCred); var o = Part(root, "claudeAiOauth");
        if (o == null) throw new AuthError(L.T("Claude Code에 로그인되어 있지 않습니다.", "Not signed in to Claude Code."), true);
        s.Plan = o.ContainsKey("subscriptionType") ? o["subscriptionType"] as string : null;
        string token = o["accessToken"] as string, body;
        try { body = ClaudeUsage(token); }
        catch (WebException e)
        {
            if (Status(e) != 401) throw;
            // 그사이 CLI·앱이 토큰을 바꿔 놨으면 그걸 쓰고, 그대로일 때만 직접 갱신한다 (서로 덮어쓰지 않게)
            root = Read(ClaudeCred); o = Part(root, "claudeAiOauth");
            if (o == null) throw new AuthError(L.T("Claude Code에 로그인되어 있지 않습니다.", "Not signed in to Claude Code."), true);
            if (o["accessToken"] as string == token) RefreshClaude(root, o);
            body = AfterRefresh(() => ClaudeUsage(o["accessToken"] as string), "Claude");
        }
        var u = D(Json.DeserializeObject(body));
        var wins = new List<Win>();
        Add(wins, u, "five_hour", L.T("현재 세션 (5시간)", "Current session (5h)"));
        Add(wins, u, "seven_day", L.T("주간 · 전체 모델", "Weekly · all models"));
        Add(wins, u, "seven_day_opus", L.T("주간 · Opus", "Weekly · Opus"));
        Add(wins, u, "seven_day_sonnet", L.T("주간 · Sonnet", "Weekly · Sonnet"));
        s.Wins = wins;
    }

    static void Add(List<Win> wins, Dictionary<string, object> u, string key, string label)
    {
        var w = new Win(label);
        var d = u.ContainsKey(key) ? D(u[key]) : null;
        if (d == null) { if (key.StartsWith("seven_day_")) return; }
        else
        {
            w.Pct = Num(d["utilization"]);
            var r = d.ContainsKey("resets_at") ? d["resets_at"] as string : null;
            if (r != null) w.Reset = DateTimeOffset.Parse(r, CultureInfo.InvariantCulture).ToLocalTime();
        }
        wins.Add(w);
    }

    static string ClaudeUsage(string token)
    {
        return Http("GET", "https://api.anthropic.com/api/oauth/usage", new Dictionary<string, string> {
            { "Authorization", "Bearer " + token }, { "anthropic-beta", "oauth-2025-04-20" } }, null);
    }

    static void RefreshClaude(Dictionary<string, object> root, Dictionary<string, object> o)
    {
        if (!o.ContainsKey("refreshToken")) throw Expired("Claude");
        var req = Json.Serialize(new Dictionary<string, object> {
            { "grant_type", "refresh_token" }, { "refresh_token", o["refreshToken"] }, { "client_id", ClaudeClientId } });
        WebException last = null; bool rejected = false;
        foreach (var url in ClaudeTokenUrls)
        {
            try
            {
                var r = D(Json.DeserializeObject(Http("POST", url, null, req)));
                o["accessToken"] = r["access_token"];
                if (r.ContainsKey("refresh_token")) o["refreshToken"] = r["refresh_token"];
                o["expiresAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Convert.ToInt64(r["expires_in"]) * 1000;
                SaveJson(ClaudeCred, root);
                return;
            }
            catch (WebException e) { last = e; rejected |= Status(e) == 400 || Status(e) == 401; }
        }
        if (rejected) throw Expired("Claude");
        throw last;
    }

    // ---------------- Codex ----------------
    const string CodexClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    public static void Codex(Svc s)
    {
        var root = Read(CodexAuth); var t = Part(root, "tokens");
        if (t == null) throw new AuthError(L.T("Codex에 ChatGPT 계정으로 로그인되어 있지 않습니다.", "Codex isn't signed in with a ChatGPT account."), true);
        string token = t["access_token"] as string, body;
        try { body = CodexUsage(t); }
        catch (WebException e)
        {
            if (Status(e) != 401) throw;
            root = Read(CodexAuth); t = Part(root, "tokens");
            if (t == null) throw new AuthError(L.T("Codex에 ChatGPT 계정으로 로그인되어 있지 않습니다.", "Codex isn't signed in with a ChatGPT account."), true);
            if (t["access_token"] as string == token) RefreshCodex(root, t);
            body = AfterRefresh(() => CodexUsage(t), "Codex");
        }
        var u = D(Json.DeserializeObject(body));
        s.Plan = u.ContainsKey("plan_type") ? u["plan_type"] as string : null;
        var rl = u.ContainsKey("rate_limit") ? D(u["rate_limit"]) : null;
        var wins = new List<Win>();
        wins.Add(CodexWin(rl == null ? null : D(rl["primary_window"]), L.T("현재 세션 (5시간)", "Current session (5h)")));
        wins.Add(CodexWin(rl == null ? null : D(rl["secondary_window"]), L.T("주간", "Weekly")));
        s.Wins = wins;
    }

    static Win CodexWin(Dictionary<string, object> d, string label)
    {
        var w = new Win(label);
        if (d == null) return w;
        w.Pct = Num(d["used_percent"]);
        if (d.ContainsKey("reset_at") && d["reset_at"] != null)
            w.Reset = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(d["reset_at"])).ToLocalTime();
        return w;
    }

    static string CodexUsage(Dictionary<string, object> t)
    {
        return Http("GET", "https://chatgpt.com/backend-api/wham/usage", new Dictionary<string, string> {
            { "Authorization", "Bearer " + t["access_token"] }, { "ChatGPT-Account-Id", t["account_id"] as string } }, null);
    }

    static void RefreshCodex(Dictionary<string, object> root, Dictionary<string, object> t)
    {
        if (!t.ContainsKey("refresh_token")) throw Expired("Codex");
        try
        {
            var req = Json.Serialize(new Dictionary<string, object> {
                { "client_id", CodexClientId }, { "grant_type", "refresh_token" }, { "refresh_token", t["refresh_token"] }, { "scope", "openid profile email" } });
            var r = D(Json.DeserializeObject(Http("POST", "https://auth.openai.com/oauth/token", null, req)));
            foreach (var k in new[] { "access_token", "refresh_token", "id_token" }) if (r.ContainsKey(k)) t[k] = r[k];
            root["last_refresh"] = DateTime.UtcNow.ToString("o");
            SaveJson(CodexAuth, root);
        }
        catch (WebException e) { if (Status(e) == 400 || Status(e) == 401) throw Expired("Codex"); throw; }
    }
}

// 화면 문구: Windows 표시 언어가 한국어면 한국어, 그 밖에는 영어
static class L
{
    public static readonly bool Ko = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";
    public static string T(string ko, string en) { return Ko ? ko : en; }
}

static class Theme
{
    public static bool Light
    {
        get
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return k != null && (k.GetValue("SystemUsesLightTheme") as int?) == 1;
        }
    }
    public static Color Fg { get { return Light ? Color.FromArgb(28, 28, 28) : Color.White; } }
    public static Color Warn = Color.FromArgb(232, 72, 85);

    // 오류 중이면 마지막 값을 흐리게 보여 준다
    public static Color Dim(Color c, bool dim) { return dim ? Color.FromArgb(140, c) : c; }

    // 요청 제한 중이면 남은 시간을 그때그때 계산해서 보여 준다
    public static int Left(Svc s) { return Math.Max(1, (int)Math.Ceiling((s.NextAt - DateTime.Now).TotalMinutes)); }
    public static string Message(Svc s)
    {
        if (s.State != State.Limited) return s.Error;
        return L.T("요청이 많아 잠시 쉬는 중입니다. " + Left(s) + "분 뒤에 다시 확인합니다.", "Too many requests. Trying again in " + Left(s) + " min.");
    }
}

class Flyout : Form
{
    readonly Svc[] svcs;
    public DateTime HiddenAt;
    readonly Font title = new Font("Segoe UI Semibold", 10.5f), body = new Font("Segoe UI", 9f), small = new Font("Segoe UI", 8f);
    readonly Font icon = new Font("Segoe MDL2 Assets", 9f);   // Windows 10/11 기본 아이콘 글꼴
    readonly float k;   // DPI 배율. 글꼴(pt)은 알아서 커지지만 간격은 직접 맞춘다
    bool fromBottom, overRefresh;
    Rectangle refreshRect;
    public bool Busy;
    public Action RefreshClicked;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    public Flyout(Svc[] s)
    {
        svcs = s;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; DoubleBuffered = true;
        using (var g = CreateGraphics()) k = g.DpiX / 96f;
        Width = S(340);
        Deactivate += delegate { Hide(); HiddenAt = DateTime.Now; };
        KeyPreview = true; KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        var tick = new System.Windows.Forms.Timer { Interval = 1000 };   // 열려 있는 동안 "N초 전"을 계속 맞춘다
        tick.Tick += delegate { Invalidate(); };
        VisibleChanged += delegate { tick.Enabled = Visible; };
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool over = refreshRect.Contains(e.Location);
        if (over != overRefresh) { overRefresh = over; Cursor = over ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (overRefresh) { overRefresh = false; Cursor = Cursors.Default; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && refreshRect.Contains(e.Location) && RefreshClicked != null) RefreshClicked();
        base.OnMouseClick(e);
    }

    int S(float v) { return (int)Math.Round(v * k); }

    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ClassStyle |= 0x20000; /* CS_DROPSHADOW */ return p; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE
    }

    public void ShowNear()
    {
        Height = Arrange(null);
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Left = Math.Max(wa.Left + S(12), Math.Min(Cursor.Position.X - Width / 2, wa.Right - Width - S(12)));
        fromBottom = Cursor.Position.Y > wa.Top + wa.Height / 2;
        Top = fromBottom ? wa.Bottom - Height - S(12) : wa.Top + S(12);
        BackColor = Theme.Light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32);
        Invalidate(); Show(); Activate();
    }

    // 열려 있는 동안 내용이 바뀌면 작업 표시줄 쪽 가장자리를 기준으로 높이만 맞춘다
    public void Refit()
    {
        int h = Arrange(null);
        if (h != Height) { int bottom = Bottom; Height = h; if (fromBottom) Top = bottom - h; }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;   // 막대 가장자리를 픽셀에 딱 맞춘다
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        Arrange(e.Graphics);
    }

    // 그리기와 높이 계산을 한 곳에서 한다 (g가 null이면 높이만 잰다)
    int Arrange(Graphics g)
    {
        var fg = Theme.Fg; var sub = Color.FromArgb(150, fg); var track = Color.FromArgb(Theme.Light ? 40 : 60, fg);
        int x = S(18), w = Width - x * 2, y = S(16);
        DateTime updated = DateTime.MinValue, checkedAt = DateTime.MinValue;
        int shown = 0;
        foreach (var s in svcs)
        {
            if (s.Checked > checkedAt) checkedAt = s.Checked;
            if (!s.Shown) continue;   // 로그인하지 않은 서비스는 우클릭 메뉴에서만 안내한다
            shown++;
            if (g != null)
            {
                using (var b = new SolidBrush(s.Brand)) g.FillEllipse(b, x, y + (title.Height - S(8)) / 2, S(8), S(8));
                TextRenderer.DrawText(g, s.Name, title, new Point(x + S(14), y), fg);
            }
            y += title.Height + S(14);
            var msg = Theme.Message(s);
            if (msg != null)
            {
                int eh = TextRenderer.MeasureText(msg, small, new Size(w, int.MaxValue), TextFormatFlags.WordBreak).Height;
                if (g != null) TextRenderer.DrawText(g, msg, small, new Rectangle(x, y, w, eh), sub, TextFormatFlags.WordBreak);
                y += eh + S(12);
            }
            foreach (var win in s.Wins)   // 일시적 오류면 마지막 값도 흐리게 함께 보여 준다
            {
                double p = win.Now;
                if (g != null)
                {
                    TextRenderer.DrawText(g, win.Label, body, new Point(x, y), fg);
                    TextRenderer.DrawText(g, p < 0 ? "–" : Math.Round(p) + L.T("% 사용", "% used"), body, new Rectangle(x, y, w, body.Height), fg, TextFormatFlags.Right);
                    Bar(g, x, y + body.Height + S(4), w, S(4), p, Theme.Dim(p >= 80 ? Theme.Warn : s.Brand, s.Error != null), track);
                    TextRenderer.DrawText(g, ResetText(win.Reset), small, new Point(x, y + body.Height + S(12)), sub);
                }
                y += body.Height + S(12) + small.Height + S(10);
            }
            if (s.Updated > updated) updated = s.Updated;
            y += S(6);
        }
        if (shown == 0)   // 둘 다 로그인 정보가 없을 때
        {
            string hint = L.T("Claude나 Codex에 로그인하면\n여기에 사용량이 나옵니다.\n우클릭해서 로그인하거나 설치할 수 있어요.",
                "Sign in to Claude Code or Codex\nand your usage shows up here.\nRight-click to sign in or install.");
            int hh = TextRenderer.MeasureText(hint, body, new Size(w, int.MaxValue), TextFormatFlags.WordBreak).Height;
            if (g != null) TextRenderer.DrawText(g, hint, body, new Rectangle(x, y, w, hh), fg, TextFormatFlags.WordBreak);
            y += hh + S(14);
        }
        // 맨 아래: 언제 갱신했는지 · 갱신 주기 · 새로고침 버튼
        int bh = Math.Max(small.Height, S(26));
        refreshRect = new Rectangle(x + w - S(26), y, S(26), bh);
        if (g != null)
        {
            string info = Busy ? L.T("갱신 중…", "Refreshing…")
                : updated != DateTime.MinValue ? L.T("마지막 갱신 ", "Updated ") + Ago(DateTime.Now - updated)
                : checkedAt != DateTime.MinValue ? L.T("마지막 확인 ", "Checked ") + Ago(DateTime.Now - checkedAt)
                : L.T("갱신 중…", "Refreshing…");
            info += L.T(" · " + App.RefreshMinutes + "분마다", " · every " + App.RefreshMinutes + " min");
            TextRenderer.DrawText(g, info, small, new Rectangle(x, y, w - S(30), bh), sub, TextFormatFlags.VerticalCenter);
            if (overRefresh) using (var hb = new SolidBrush(Color.FromArgb(Theme.Light ? 18 : 28, fg))) g.FillRectangle(hb, refreshRect);
            TextRenderer.DrawText(g, "", icon, refreshRect, Busy ? sub : fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        return y + bh + S(12);
    }

    static void Bar(Graphics g, int x, int y, int w, int h, double p, Color fill, Color track)
    {
        using (var t = new SolidBrush(track)) g.FillRectangle(t, x, y, w, h);
        if (p > 0) using (var f = new SolidBrush(fill)) g.FillRectangle(f, x, y, (int)(w * Math.Min(p, 100) / 100), h);
    }

    static string ResetText(DateTimeOffset? r)
    {
        if (!r.HasValue) return "";
        var left = r.Value - DateTimeOffset.Now;
        if (left.Ticks < 0) return L.T("리셋됨", "Reset");
        // Windows 지역 설정의 표준 표기를 따른다. 오늘이면 시간만, 아니면 날짜·요일까지
        // (예: 10월 26일 (월) 오후 3:20 / Mon, October 26 3:20 PM / 26. Oktober (Mo) 15:20)
        var c = CultureInfo.CurrentCulture; var f = c.DateTimeFormat;
        string when = r.Value.Date == DateTimeOffset.Now.Date ? r.Value.ToString(f.ShortTimePattern)
            : c.TwoLetterISOLanguageName == "en" ? r.Value.ToString("ddd, " + f.MonthDayPattern + " " + f.ShortTimePattern)
            : r.Value.ToString(f.MonthDayPattern + " (ddd) " + f.ShortTimePattern);
        return L.T("리셋 " + when + " · " + Dur(left) + " 후", "Resets " + when + " · in " + Dur(left));
    }

    static string Dur(TimeSpan t)
    {
        if (t.TotalDays >= 1) return L.T((int)t.TotalDays + "일 " + t.Hours + "시간", (int)t.TotalDays + "d " + t.Hours + "h");
        if (t.TotalHours >= 1) return L.T((int)t.TotalHours + "시간 " + t.Minutes + "분", (int)t.TotalHours + "h " + t.Minutes + "m");
        return Math.Max(1, t.Minutes) + L.T("분", "m");
    }

    static string Ago(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return Math.Max(0, (int)t.TotalSeconds) + L.T("초 전", "s ago");
        if (t.TotalMinutes < 60) return (int)t.TotalMinutes + L.T("분 전", "m ago");
        return (int)t.TotalHours + L.T("시간 전", "h ago");
    }
}

class About : Form
{
    public About()
    {
        Text = L.T("정보", "About"); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(640, 260); ShowInTaskbar = false;
        KeyPreview = true; KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("banner.png");
        if (st != null) Controls.Add(new PictureBox { Image = Image.FromStream(st), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(0, 0, 640, 200) });
        var link = new LinkLabel { Text = "v" + App.Version.ToString(3) + "  ·  github.com/" + App.Repo, AutoSize = true, Location = new Point(16, 222) };
        link.LinkClicked += delegate { App.Open("https://github.com/" + App.Repo); };
        Controls.Add(link);
    }
}

// 알림 영역 바로 왼쪽에 붙는 투명 창. 로그인된 서비스마다 숫자 2줄(5시간/주간) + 게이지.
// Windows 11에는 deskband가 없어서 topmost 창을 작업 표시줄 바로 위 z-order에 계속 끼워 둔다.
// (작업 표시줄을 owner로 삼으면 탐색기와 입력이 묶여, 이 앱이 멈출 때 작업 표시줄까지 멈춘다)
class TaskbarWidget : Form
{
    public readonly IntPtr Taskbar;
    readonly Svc[] svcs;
    Rectangle bounds;
    bool hover;

    struct RECT { public int L, T, R, B; }
    struct POINT { public int X, Y; }
    struct SIZE { public int W, H; }
    struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll")] static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr p, IntPtr after, string c, string n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, int flags);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE sz, IntPtr src, ref POINT srcPt, int key, ref BLENDFUNCTION bf, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

    public static IntPtr FindTaskbar() { return FindWindow("Shell_TrayWnd", null); }

    public TaskbarWidget(IntPtr taskbar, Svc[] s)
    {
        Taskbar = taskbar; svcs = s;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        MouseEnter += delegate { hover = true; Redraw(); };
        MouseLeave += delegate { hover = false; Redraw(); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x80000 | 0x80 | 0x8 | 0x8000000; return p; }   // LAYERED | TOOLWINDOW | TOPMOST | NOACTIVATE
    }

    // 0.25초마다(그리고 데이터가 바뀔 때): 작업 표시줄과 알림 영역 위치·보여 줄 서비스 수를 따라간다
    public void Follow(bool redraw)
    {
        RECT tb, tray;
        var notify = FindWindowEx(Taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (!GetWindowRect(Taskbar, out tb) || notify == IntPtr.Zero || !GetWindowRect(notify, out tray) || tb.B - tb.T > tb.R - tb.L)
        { Hide(); return; }   // ponytail: 세로 작업 표시줄(Windows 10)은 숨김. 필요하면 알림 영역 위쪽 배치 추가
        int h = tb.B - tb.T, width = WidgetWidth(Em(h), Blocks().Count);
        var r = new Rectangle(tray.L - width, tb.T, width, h);
        if (!Visible) Show();
        FixZ();
        if (redraw || r != bounds) { bounds = r; Redraw(); }
    }

    // 작업 표시줄 바로 위에만 둔다: 작업 표시줄이 앞으로 오면 따라 올라가고, 그 위에 뜨는 메뉴는 가리지 않는다
    public void FixZ()
    {
        if (!IsHandleCreated) return;
        var above = GetWindow(Taskbar, 3);   // GW_HWNDPREV
        if (above != Handle) SetWindowPos(Handle, above == IntPtr.Zero ? (IntPtr)(-1) : above, 0, 0, 0, 0, 0x13);   // NOSIZE | NOMOVE | NOACTIVATE
    }

    // 로그인된 서비스만 칸을 만든다. 하나도 없으면 빈 칸 하나(클릭하면 안내)
    List<Svc> Blocks()
    {
        var l = new List<Svc>();
        foreach (var s in svcs) if (s.Shown) l.Add(s);
        if (l.Count == 0) l.Add(null);
        return l;
    }

    // 작업 표시줄 48px일 때 숫자 약 11.7px (8.75pt)
    static float Em(int h) { return h * 35f / 144f; }
    static int R(double v) { return (int)Math.Round(v); }
    static int WidgetWidth(float em, int blocks) { return R(em * 0.6) * 2 + R(em * 2.3) * blocks + R(em * 0.8) * (blocks - 1); }

    public void Redraw()
    {
        if (bounds.Width == 0 || !IsHandleCreated) return;
        using (var bmp = Draw(bounds.Size)) Push(bmp, bounds.Location);
    }

    Bitmap Draw(Size sz)
    {
        float em = Em(sz.Height);
        int bw = R(em * 2.3), gap = R(em * 0.8), pad = R(em * 0.6);
        int bar = Math.Max(2, R(sz.Height / 24.0)), digit = R(em * 0.7);   // Segoe UI 숫자 높이 ≈ 0.7em
        int rowH = digit + Math.Max(2, R(em / 5)) + bar, rowGap = Math.Max(3, R(em * 0.35));
        int top = (sz.Height - rowH * 2 - rowGap) / 2, inset = Math.Max(2, sz.Height / 12);
        var fg = Theme.Fg;
        var bmp = new Bitmap(sz.Width, sz.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var text = new SolidBrush(fg))
        using (var track = new SolidBrush(Color.FromArgb(Theme.Light ? 50 : 70, fg)))
        using (var bg = new SolidBrush(Color.FromArgb(hover ? (Theme.Light ? 16 : 22) : 1, fg)))   // 알파 0인 픽셀은 클릭이 통과하므로 최소 1
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;   // 막대 가장자리를 픽셀에 딱 맞춘다
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var path = Round(new Rectangle(0, inset, sz.Width, sz.Height - inset * 2), inset)) g.FillPath(bg, path);
            float ascent = font.FontFamily.GetCellAscent(font.Style) * em / font.FontFamily.GetEmHeight(font.Style);
            var fmt = StringFormat.GenericTypographic;
            var blocks = Blocks();
            for (int c = 0; c < blocks.Count; c++)
            {
                var s = blocks[c];
                int x = pad + c * (bw + gap);
                var rows = s == null ? new Win[2] : new[] { s.Short, s.Week };
                bool dim = s == null || s.Error != null;   // 빈 칸이거나 오류 중이면 흐리게
                text.Color = Theme.Dim(fg, dim);
                for (int i = 0; i < 2; i++)
                {
                    var w = rows[i];
                    double p = w == null ? -1 : w.Now;
                    string t = p < 0 ? "-" : Math.Round(p).ToString();
                    int y = top + i * (rowH + rowGap);
                    float tw = g.MeasureString(t, font, PointF.Empty, fmt).Width;
                    g.DrawString(t, font, text, (float)Math.Round(x + (bw - tw) / 2), y + digit - ascent, fmt);   // 숫자 아래선을 y+digit에 맞춘다
                    int by = y + rowH - bar;
                    g.FillRectangle(track, x, by, bw, bar);
                    if (p > 0) using (var fill = new SolidBrush(Theme.Dim(p >= 80 ? Theme.Warn : s.Brand, dim)))
                        g.FillRectangle(fill, x, by, Math.Max(1, R(bw * Math.Min(p, 100) / 100)), bar);
                }
            }
        }
        return bmp;
    }

    static GraphicsPath Round(Rectangle r, int rad)
    {
        var p = new GraphicsPath(); int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // 반투명 비트맵을 그대로 화면에 올린다 (per-pixel alpha)
    void Push(Bitmap bmp, Point at)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
        var dst = new POINT { X = at.X, Y = at.Y }; var src = new POINT(); var size = new SIZE { W = bmp.Width, H = bmp.Height };
        var blend = new BLENDFUNCTION { Alpha = 255, Format = 1 };   // AC_SRC_ALPHA
        UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2);   // ULW_ALPHA
        SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
    }
}

class App : ApplicationContext
{
    public const string Repo = "haengseon/ClaudeCodexUsageWinTray";
    public static readonly Version Version = Assembly.GetExecutingAssembly().GetName().Version;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "ClaudeCodexUsageWinTray";
    const string SettingsKey = @"Software\ClaudeCodexUsageWinTray";
    static readonly int[] Intervals = { 1, 2, 5, 10, 15, 30, 60 };   // 비슷한 앱들(CodexBar 등)도 1~30분, 기본 5분

    // 로그인 방법: CLI가 있으면 CLI, 없으면 데스크톱 앱(claude:// · codex:// · chatgpt://), 그것도 없으면 내려받기 페이지
    readonly Svc claude = new Svc { Name = "Claude", Brand = Color.FromArgb(217, 119, 87), Cli = "claude", LoginArgs = "auth login",
        AppScheme = "claude", Download = "https://claude.com/download" };
    readonly Svc codex = new Svc { Name = "Codex", Brand = Color.FromArgb(16, 163, 127), Cli = "codex", LoginArgs = "login",
        AppScheme = "codex", AltScheme = "chatgpt", Download = "https://developers.openai.com/codex" };
    readonly Svc[] svcs;
    readonly Flyout flyout;
    ContextMenuStrip menu;
    TaskbarWidget widget;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer(), dock = new System.Windows.Forms.Timer(), loginWatch = new System.Windows.Forms.Timer();
    readonly SynchronizationContext ui;
    DateTime lastUpdateCheck = DateTime.MinValue, loginUntil;
    Release available;   // 찾았지만 아직 설치하지 않은 새 버전 (수동 업데이트일 때)
    DateTime[] loginStamps;
    int busy;

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    readonly WinEventProc onForeground;   // 콜백이 GC되지 않게 붙잡아 둔다

    public App()
    {
        ui = new WindowsFormsSynchronizationContext();
        svcs = new[] { claude, codex };
        // 네트워크 전에 로그인 정보가 있는지만 먼저 봐서 처음부터 알맞은 칸 수로 그린다
        claude.State = Api.HasLogin(Api.ClaudeCred, "claudeAiOauth") ? State.Checking : State.Login;
        codex.State = Api.HasLogin(Api.CodexAuth, "tokens") ? State.Checking : State.Login;
        flyout = new Flyout(svcs);
        flyout.RefreshClicked = () => Refresh(true);
        timer.Interval = RefreshMinutes * 60000; timer.Tick += delegate { Refresh(); }; timer.Start();
        dock.Interval = 250; dock.Tick += delegate { Dock(false); }; dock.Start();
        loginWatch.Interval = 3000; loginWatch.Tick += delegate { WatchLogin(); };
        // 작업 표시줄이 앞으로 나오면(클릭 등) 바로 위젯을 다시 그 위에 끼운다
        onForeground = delegate { try { if (widget != null) widget.FixZ(); } catch { } };   // 네이티브 콜백이라 예외가 새면 앱이 죽는다
        SetWinEventHook(3, 3, IntPtr.Zero, onForeground, 0, 0, 0);   // EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT
        SystemEvents.UserPreferenceChanged += delegate { Redraw(); };
        Dock(false);
        Refresh();
    }

    // 탐색기가 재시작되면 작업 표시줄 창이 바뀌므로 위젯을 새로 만든다
    void Dock(bool redraw)
    {
        var tb = TaskbarWidget.FindTaskbar();
        if (widget != null && (widget.IsDisposed || widget.Taskbar != tb)) { widget.Dispose(); widget = null; }
        if (widget == null && tb != IntPtr.Zero)
        {
            widget = new TaskbarWidget(tb, svcs);
            widget.MouseUp += (o, e) =>
            {
                if (e.Button == MouseButtons.Right) ShowMenu();
                else if (flyout.Visible) flyout.Hide();
                else if ((DateTime.Now - flyout.HiddenAt).TotalMilliseconds > 400) flyout.ShowNear();
            };
        }
        if (widget != null) widget.Follow(redraw);
    }

    void Redraw()
    {
        Dock(true);
        if (flyout.Visible) flyout.Refit();
    }

    void ShowMenu()
    {
        if (menu != null) menu.Dispose();
        menu = new ContextMenuStrip();
        foreach (var s in svcs) menu.Items.Add(AuthItem(s));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("새로고침", "Refresh"), null, delegate { Refresh(true); });
        var every = new ToolStripMenuItem(L.T("갱신 간격", "Refresh interval"));
        int current = RefreshMinutes;
        foreach (var m in Intervals)
        {
            int min = m;
            every.DropDownItems.Add(new ToolStripMenuItem(min + L.T("분", " min") + (min == 5 ? L.T(" (기본)", " (default)") : ""), null,
                delegate { RefreshMinutes = min; timer.Interval = min * 60000; }) { Checked = min == current });
        }
        menu.Items.Add(every);
        menu.Items.Add(new ToolStripMenuItem(L.T("Windows 시작 시 자동 실행", "Start with Windows"), null, delegate { AutoStart = !AutoStart; }) { Checked = AutoStart });
        var update = new ToolStripMenuItem(available != null ? L.T("업데이트 (새 버전 있음)", "Update (new version)") : L.T("업데이트", "Update"));
        update.DropDownItems.Add(new ToolStripMenuItem(L.T("자동 업데이트", "Update automatically"), null, delegate { AutoUpdate = !AutoUpdate; }) { Checked = AutoUpdate });
        update.DropDownItems.Add(available != null
            ? new ToolStripMenuItem(available.Tag + L.T(" 설치", " — install now"), null, delegate { var r = available; Task.Run(() => Install(r)); })
            : new ToolStripMenuItem(L.T("지금 확인", "Check now"), null, delegate { CheckNow(); }));
        update.DropDownItems.Add(new ToolStripMenuItem(L.T("현재 버전 v", "Current version v") + Version.ToString(3)) { Enabled = false });
        menu.Items.Add(update);
        menu.Items.Add(L.T("정보", "About"), null, delegate { using (var a = new About()) a.ShowDialog(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("종료", "Quit"), null, delegate { Application.Exit(); });
        SetForegroundWindow(widget.Handle);   // 메뉴 밖을 클릭하면 닫히도록
        menu.Show(Cursor.Position);
    }

    // 로그인돼 있으면 상태만 보여 주고(누를 수 없음), 아니면 설치된 것에 맞춰 로그인 / 다시 로그인 / 설치하기
    ToolStripItem AuthItem(Svc s)
    {
        string plan = string.IsNullOrEmpty(s.Plan) ? "" : " (" + char.ToUpper(s.Plan[0]) + s.Plan.Substring(1) + ")";
        string via = s.How == Setup.App ? L.T(" (앱에서)", " (in the app)") : "";
        string text; bool action = true;
        switch (s.State)
        {
            case State.Ok: text = s.Name + L.T(" 로그인됨", " signed in") + plan; action = false; break;
            case State.Limited: text = s.Name + plan + L.T(" · 요청 제한, " + Theme.Left(s) + "분 뒤 다시 확인", " · rate limited, retrying in " + Theme.Left(s) + " min"); action = false; break;
            case State.Checking: text = s.Name + L.T(" 확인 중…", " checking…"); action = false; break;
            case State.Login:
                text = s.How == Setup.None ? s.Name + L.T(" 설치하기…", " get the app…") : s.Name + L.T(" 로그인…", " sign in…") + via; break;
            case State.Relogin: text = s.Name + L.T(" 다시 로그인… (인증 만료)", " sign in again… (expired)") + via; break;
            default: text = s.Name + L.T(" 다시 로그인… (연결 오류)", " sign in again… (connection error)") + via; break;
        }
        var item = new ToolStripMenuItem(text, Dot(s.Brand)) { Enabled = action };
        if (action) item.Click += delegate { Login(s); };
        return item;
    }

    static Image Dot(Color c)
    {
        var b = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(b))
        using (var br = new SolidBrush(c)) { g.SmoothingMode = SmoothingMode.AntiAlias; g.FillEllipse(br, 4, 4, 8, 8); }
        return b;
    }

    // 로그인 창을 띄우고, 로그인 정보 파일이 바뀌는지 10분 동안 지켜보다가 바뀌면 바로 다시 읽는다
    void Login(Svc s)
    {
        var how = s.How; var target = s.HowTarget; var args = s.LoginArgs; var download = s.Download;
        Task.Run(() =>   // 프로그램 실행은 UI 밖에서 (느린 PATH·셸 연결 때문에 멈추지 않게)
        {
            if (how == Setup.Cli) Open("cmd.exe", "/c \"\"" + target + "\" " + args + " & pause\"");
            else Open(how == Setup.App ? target : download, null);
        });
        loginStamps = Stamps(); loginUntil = DateTime.Now.AddMinutes(10); loginWatch.Start();
    }

    void WatchLogin()
    {
        if (DateTime.Now > loginUntil) { loginWatch.Stop(); return; }
        var now = Stamps();
        for (int i = 0; i < now.Length; i++)
            if (now[i] != loginStamps[i]) { loginStamps = now; Refresh(true); return; }
    }

    static DateTime[] Stamps()
    {
        return new[] { File.GetLastWriteTimeUtc(Api.ClaudeCred), File.GetLastWriteTimeUtc(Api.CodexAuth) };   // 파일이 없으면 고정값
    }

    public static void Open(string file, string args = null)
    {
        try { Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true }); } catch { }
    }

    // 이 PC에서 로그인할 방법 찾기 (갱신 스레드에서 호출)
    static void Detect(Svc s)
    {
        var cli = Which(s.Cli);
        if (cli != null) { s.How = Setup.Cli; s.HowTarget = cli; return; }
        foreach (var scheme in new[] { s.AppScheme, s.AltScheme })
            if (scheme != null) using (var k = Registry.ClassesRoot.OpenSubKey(scheme))
                if (k != null && k.GetValue("URL Protocol") != null) { s.How = Setup.App; s.HowTarget = scheme + "://"; return; }
        s.How = Setup.None; s.HowTarget = null;
    }

    // PATH에서 실행 파일 찾기 (npm 설치는 .cmd, 네이티브 설치는 .exe)
    static string Which(string name)
    {
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';');
        foreach (var raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0 || !Path.IsPathRooted(dir)) continue;
            foreach (var ext in exts)
            {
                if (ext.Length == 0) continue;
                try { var f = Path.Combine(dir, name + ext); if (File.Exists(f)) return f; }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    // 갱신 간격(분, 1~60). 바꿨을 때만 레지스트리에 남긴다 — 파일은 만들지 않는다
    public static int RefreshMinutes
    {
        get
        {
            using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                var v = k == null ? null : k.GetValue("RefreshMinutes") as int?;
                return v.HasValue && v.Value >= 1 && v.Value <= 60 ? v.Value : 5;
            }
        }
        set { using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue("RefreshMinutes", value, RegistryValueKind.DWord); }
    }

    // 자동 업데이트 켬/끔 (기본 켬). 끄면 하루 한 번 확인만 하고 설치는 메뉴에서 직접 한다
    static bool AutoUpdate
    {
        get { using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey)) return k == null || (k.GetValue("AutoUpdate") as int?) != 0; }
        set { using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue("AutoUpdate", value ? 1 : 0, RegistryValueKind.DWord); }
    }

    // 메뉴의 "지금 확인": 결과를 알려 주고, 새 버전이 있으면 설치할지 묻는다
    void CheckNow()
    {
        Task.Run(() =>
        {
            Release r = null; bool failed = false;
            try { r = Updater.Find(); } catch { failed = true; }
            lastUpdateCheck = DateTime.Now; available = r;
            ui.Post(delegate
            {
                if (failed) Ask(L.T("업데이트를 확인하지 못했습니다. 네트워크를 확인해 주세요.", "Couldn't check for updates. Please check your network."), false);
                else if (r == null) Ask(L.T("최신 버전입니다 (v", "You're up to date (v") + Version.ToString(3) + ").", false);
                else if (Ask(r.Tag + L.T(" 버전이 있습니다. 지금 설치할까요?\n설치하면 프로그램이 잠깐 다시 시작됩니다.", " is available. Install it now?\nThe app restarts briefly."), true))
                    Task.Run(() => Install(r));
            }, null);
        });
    }

    void Install(Release r)
    {
        if (!Updater.Install(r, ui))
            ui.Post(delegate { Ask(L.T("업데이트를 설치하지 못했습니다. 잠시 뒤 다시 시도해 주세요.", "Couldn't install the update. Please try again later."), false); }, null);
    }

    // 작업 표시줄 위에서 띄우는 알림창이 다른 창 뒤로 숨지 않게 맨 위 소유 창을 붙인다
    static bool Ask(string text, bool yesNo)
    {
        using (var owner = new Form { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Size = new Size(0, 0), StartPosition = FormStartPosition.CenterScreen })
        {
            owner.Show();
            return MessageBox.Show(owner, text, "ClaudeCodexUsageWinTray", yesNo ? MessageBoxButtons.YesNo : MessageBoxButtons.OK,
                yesNo ? MessageBoxIcon.Question : MessageBoxIcon.Information) == DialogResult.Yes;
        }
    }

    static bool AutoStart
    {
        get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
        set
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (value) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\""); else k.DeleteValue(RunName, false);
        }
    }

    void Refresh(bool force = false)
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        ui.Post(delegate { flyout.Busy = true; if (flyout.Visible) flyout.Invalidate(); }, null);
        Task.Run(() =>
        {
            try
            {
                foreach (var s in svcs)
                {
                    Fetch(s, s == claude ? (Action<Svc>)Api.Claude : Api.Codex, force);
                    if (s.State != State.Ok) Detect(s);   // 로그인 메뉴가 필요할 때만 찾는다
                }
            }
            finally { busy = 0; ui.Post(delegate { flyout.Busy = false; Redraw(); }, null); }
            if (DateTime.Now - lastUpdateCheck > TimeSpan.FromHours(24))
            {   // 하루 한 번 확인: 자동 업데이트면 바로 설치, 아니면 메뉴에 "설치" 항목만 띄운다
                lastUpdateCheck = DateTime.Now;
                try { available = Updater.Find(); } catch { }
                if (available != null && AutoUpdate) Updater.Install(available, ui);
            }
        });
    }

    static void Fetch(Svc s, Action<Svc> f, bool force)
    {
        if (!force && DateTime.Now.AddSeconds(5) < s.NextAt) return;
        s.Checked = DateTime.Now;
        try { f(s); s.Error = null; s.State = State.Ok; s.Updated = DateTime.Now; s.Backoff = 0; }
        catch (AuthError e) { s.Error = e.Message; s.State = e.Missing ? State.Login : State.Relogin; s.Wins = new List<Win>(); }
        catch (Exception e)
        {
            var we = e as WebException;
            if (we != null && Api.Status(we) == 429)
            {   // 너무 자주 물으면 잠깐 막힌다: 마지막 값은 그대로 두고 간격을 두 배씩(최대 60분) 늘린다
                if (!force || s.Backoff == 0) s.Backoff = Math.Min(60, Math.Max(s.Backoff, RefreshMinutes) * 2);   // 직접 누른 새로고침은 더 늘리지 않는다
                s.NextAt = DateTime.Now.AddMinutes(s.Backoff);
                s.Error = ""; s.State = State.Limited;   // 문구는 남은 시간을 그릴 때 계산한다
                return;
            }
            s.Error = we != null ? L.T("네트워크 오류: ", "Network error: ") + e.Message : e.Message;
            s.State = State.Error;
        }
    }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        SetProcessDPIAware();
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.3
        bool updated = Array.IndexOf(args, "--updated") >= 0;
        using (var mutex = new Mutex(false, "ClaudeCodexUsageWinTray"))
        {
            bool owned;
            try { owned = mutex.WaitOne(updated ? 15000 : 0); }
            catch (AbandonedMutexException) { owned = true; }   // 이전 실행이 놓지 않고 끝났어도 넘겨받은 것
            if (!owned) return;
            try
            {
                Updater.Cleanup();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate { };   // 화면 쪽 예외로 오류 창을 띄우지 않는다
                Application.Run(new App());
            }
            finally { mutex.ReleaseMutex(); }   // 업데이트로 새로 뜬 실행이 바로 넘겨받게
        }
    }
}

// 같은 폴더의 exe를 GitHub 최신 릴리스로 교체한다. 실행 중인 exe도 이름 변경은 가능하다.
static class Updater
{
    static string Exe { get { return Application.ExecutablePath; } }

    // 업데이트 직후엔 이전 프로세스가 아직 끝나는 중이라 .old가 잠겨 있다: 30초 동안 1초마다 다시 지운다
    public static void Cleanup()
    {
        Task.Run(() =>
        {
            for (int i = 0; i < 30; i++)
            {
                bool left = false;
                foreach (var f in new[] { Exe + ".old", Exe + ".new" })
                    try { if (File.Exists(f)) File.Delete(f); } catch { left = true; }
                if (!left) return;
                Thread.Sleep(1000);
            }
        });
    }

    // 지금보다 새 릴리스가 있으면 그 정보를, 없으면 null (네트워크 오류는 예외로 올린다)
    public static Release Find()
    {
        var json = new JavaScriptSerializer();
        var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + App.Repo + "/releases/latest");
        req.UserAgent = "ClaudeCodexUsageWinTray"; req.Accept = "application/vnd.github+json"; req.Timeout = 15000;
        string body;
        using (var res = req.GetResponse()) using (var r = new StreamReader(res.GetResponseStream())) body = r.ReadToEnd();
        var rel = (Dictionary<string, object>)json.DeserializeObject(body);
        var tag = (string)rel["tag_name"];
        if (new Version(tag.TrimStart('v', 'V')) <= App.Version) return null;   // 1.0.0 은 1.0.0.0 보다 작게 비교되어 같은 버전도 건너뛴다
        foreach (Dictionary<string, object> a in (object[])rel["assets"])
            if (((string)a["name"]).EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return new Release { Tag = tag, Url = (string)a["browser_download_url"], Size = Convert.ToInt64(a["size"]),
                    Digest = a.ContainsKey("digest") ? a["digest"] as string : null };
        return null;
    }

    // 내려받아 검사한 뒤 같은 자리에서 교체하고 새 버전을 띄운다. 실패하면 false (원래 exe는 그대로)
    public static bool Install(Release rel, SynchronizationContext ui)
    {
        string tmp = Exe + ".new", old = Exe + ".old";
        try
        {
            using (var wc = new WebClient()) { wc.Headers["User-Agent"] = "ClaudeCodexUsageWinTray"; wc.DownloadFile(rel.Url, tmp); }
            var bytes = File.ReadAllBytes(tmp);
            bool ok = bytes.Length == rel.Size && bytes.Length > 2 && bytes[0] == 'M' && bytes[1] == 'Z';
            if (ok && rel.Digest != null && rel.Digest.StartsWith("sha256:"))   // GitHub가 주는 sha256 값이 있으면 맞춰 본다
                using (var sha = SHA256.Create())
                    ok = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").Equals(rel.Digest.Substring(7), StringComparison.OrdinalIgnoreCase);
            if (!ok) { File.Delete(tmp); return false; }
            if (File.Exists(old)) File.Delete(old);
            File.Move(Exe, old);
            try { File.Move(tmp, Exe); }
            catch { File.Move(old, Exe); throw; }
            Process.Start(Exe, "--updated");
            ui.Post(delegate { Application.Exit(); }, null);
            return true;
        }
        catch { try { File.Delete(tmp); } catch { } return false; }
    }
}

class Release { public string Tag, Url, Digest; public long Size; }
