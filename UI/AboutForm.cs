using System.Reflection;
using System.Windows.Forms;

namespace AITranslator.UI;

/// <summary>
/// 「關於」對話框。這裡刻意做成純字串排版，方便自行修改：
/// 要改文案、加連結、放作者資訊，直接改下方 Constants 區塊與 BuildBody() 即可。
/// </summary>
public class AboutForm : Form
{
    /// <summary>【自行修改區】想改什麼就改這裡的常數。</summary>
    private static class Constants
    {
        public const string AppName = "AITranslator";
        /// <summary>簡介文字；\n 為換行。</summary>
        public const string Description =
            "選字即譯工具：全選文字後按快捷鍵，在滑鼠旁彈出視窗顯示\n本地 Ollama 模型的繁體中文翻譯。";
        /// <summary>專案網址（顯示為可點擊連結）。</summary>
        public const string RepoUrl = "https://github.com/ramwong/AITranslator";
    }

    public AboutForm()
    {
        Text = $"關於 {Constants.AppName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 200);

        var nameLabel = new Label
        {
            Text = Constants.AppName,
            Font = new Font(Font.FontFamily, 16f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(16, 12),
        };

        var versionLabel = new Label
        {
            Text = $"版本 {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}",
            AutoSize = true,
            Location = new Point(20, 44),
        };

        var descriptionLabel = new Label
        {
            Text = Constants.Description,
            AutoSize = true,
            Location = new Point(16, 70),
        };

        var repoLink = new LinkLabel
        {
            Text = Constants.RepoUrl,
            AutoSize = true,
            Location = new Point(16, 120),
        };
        repoLink.Links.Add(0, repoLink.Text.Length, new Uri(Constants.RepoUrl));
        repoLink.LinkClicked += (_, e) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Link!.LinkData!.ToString()!,
                UseShellExecute = true,
            });

        var closeButton = new Button
        {
            Text = "確定",
            DialogResult = DialogResult.OK,
            Location = new Point(320, 160),
            Size = new Size(80, 30),
        };

        Controls.AddRange([nameLabel, versionLabel, descriptionLabel, repoLink, closeButton]);
        AcceptButton = closeButton;
    }
}