using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("随身赊账本")]
[assembly: AssemblyVersion("1.3.2.0")]

namespace SuishenLedger
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 1 && args[0] == "--update-ready")
            {
                File.WriteAllText(args[1], "ready");
                args = new string[0];
            }
            if (args.Length > 0 && args[0] == "--self-test")
            {
                SelfTest.Run();
                return;
            }
            if (args.Length > 1 && args[0] == "--ui-smoke")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                UiSmoke.Run(args[1], args.Length > 2 ? args[2] : "客户");
                return;
            }
            if (args.Length > 1 && args[0] == "--pdf-smoke")
            {
                PdfSmoke.Run(args[1], args.Length > 2 ? args[2] : "sale");
                return;
            }
            if (args.Length > 1 && args[0] == "--xlsx-smoke")
            {
                XlsxWriter.Write(args[1], args.Length > 2 && args[2] == "statement" ? PdfSmoke.SampleStatement() : PdfSmoke.SampleSale());
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "SuishenLedger_" + Hash(AppDomain.CurrentDomain.BaseDirectory), out created))
            {
                if (!created)
                {
                    MessageBox.Show("随身赊账本已经打开。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    var store = LedgerStore.OpenInteractive();
                    if (store != null) Application.Run(new MainForm(store));
                }
                catch (Exception ex)
                {
                    MessageBox.Show("软件无法启动：\r\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").Substring(0, 16);
        }
    }

    [Serializable]
    public class LedgerData
    {
        public int SchemaVersion = 2;
        public string ShopName = "我的店铺";
        public string ShopPhone = "";
        public string ShopAddress = "";
        public string GitHubRepository = "";
        public string LastBackupUtc = "";
        public List<Customer> Customers = new List<Customer>();
        public List<Product> Products = new List<Product>();
        public List<LedgerEntry> Entries = new List<LedgerEntry>();
        public List<AuditRecord> Audit = new List<AuditRecord>();
    }

    [Serializable]
    public class Customer
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "";
        public string Phone = "";
        public string Address = "";
        public string Note = "";
        public long OpeningCents;
        public string OpeningDate = DateTime.Today.ToString("yyyy-MM-dd");
        public bool Active = true;
        public override string ToString() { return Name + (Phone.Length == 0 ? "" : "  " + Phone); }
    }

    [Serializable]
    public class Product
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "";
        public string ShortName = "";
        public string Unit = "个";
        public long PriceCents;
        public bool Active = true;
        public override string ToString() { return Name; }
    }

    [Serializable]
    public class LedgerEntry
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string CustomerId = "";
        public string Kind = "sale";
        public string Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        public string Details = "";
        public long AmountCents;
        public string Note = "";
        public List<SaleItem> Items = new List<SaleItem>();
        public bool Deleted;
        public string CreatedUtc = DateTime.UtcNow.ToString("o");
        public string ModifiedUtc = "";
    }

    [Serializable]
    public class SaleItem
    {
        public string FullName = "";
        public string ShortName = "";
        public string Unit = "个";
        public int Quantity;
        public int PieceCount;
        public long PriceCents;
        public long AmountCents;
        public string Note = "";
    }

    [Serializable]
    public class AuditRecord
    {
        public string AtUtc = DateTime.UtcNow.ToString("o");
        public string Action = "";
        public string EntityType = "";
        public string EntityId = "";
        public string Reason = "";
        public string Before = "";
        public string After = "";
    }

    public static class Money
    {
        public static long Parse(string value)
        {
            decimal amount;
            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out amount) &&
                !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
                throw new FormatException("金额格式不正确。请输入例如 12.50。");
            if (amount < 0 || amount > 999999999m) throw new FormatException("金额必须在 0 到 999999999 之间。");
            return checked((long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero));
        }

        public static string Text(long cents) { return (cents / 100m).ToString("N2"); }
        public static string Label(long balance)
        {
            return balance >= 0 ? "欠款 ¥" + Text(balance) : "客户结余 ¥" + Text(-balance);
        }
    }

    public sealed class LedgerStore
    {
        const int Iterations = 200000;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("SZB1");
        readonly string dataDir;
        readonly string filePath;
        string password;
        public LedgerData Data { get; private set; }

        LedgerStore(string password, LedgerData data, string directory = null)
        {
            this.password = password;
            Data = data;
            dataDir = directory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            filePath = Path.Combine(dataDir, "ledger.dat");
        }

        internal static LedgerStore CreateForTest(LedgerData data) { return new LedgerStore("test-password", data, Path.Combine(Path.GetTempPath(), "SuishenLedgerTests", Guid.NewGuid().ToString("N"))); }
        internal static LedgerStore CreateForTest(LedgerData data, string directory) { return new LedgerStore("test-password", data, directory); }
        public string DataDirectory { get { return dataDir; } }

        public static LedgerStore OpenInteractive()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            string path = Path.Combine(dir, "ledger.dat");
            Directory.CreateDirectory(dir);
            EnsureWritable(dir);

            if (!File.Exists(path))
            {
                using (var form = new PasswordForm(true))
                {
                    if (form.ShowDialog() != DialogResult.OK) return null;
                    var fresh = new LedgerStore(form.PasswordValue, new LedgerData());
                    fresh.Save();
                    return fresh;
                }
            }

            while (true)
            {
                using (var form = new PasswordForm(false))
                {
                    if (form.ShowDialog() != DialogResult.OK) return null;
                    try
                    {
                        int oldVersion;
                        LedgerData data = Read(path, form.PasswordValue, out oldVersion);
                        var opened = new LedgerStore(form.PasswordValue, data);
                        if (oldVersion < 2)
                        {
                            string upgradeBackup = Path.Combine(dir, "升级前备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".szbbackup");
                            File.Copy(path, upgradeBackup, false);
                            opened.Audit("升级", "账本", "ledger", "账本结构从 v" + oldVersion + " 升级到 v2", null, null);
                            opened.Save();
                        }
                        return opened;
                    }
                    catch (CryptographicException)
                    {
                        string previous = path + ".previous";
                        if (File.Exists(previous))
                        {
                            try
                            {
                                LedgerData recovered = Read(previous, form.PasswordValue);
                                if (MessageBox.Show("当前账本无法打开，但上一份自动副本有效。是否恢复上一份？", "发现可用副本", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                                {
                                    var recoveredStore = new LedgerStore(form.PasswordValue, recovered);
                                    recoveredStore.Audit("恢复", "账本", "ledger", "当前文件损坏，恢复上一份自动副本", null, null);
                                    recoveredStore.Save();
                                    return recoveredStore;
                                }
                            }
                            catch (CryptographicException) { }
                        }
                        if (MessageBox.Show("密码不正确或账本已损坏。是否重试？", "无法打开", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry)
                            return null;
                    }
                }
            }
        }

        static void EnsureWritable(string dir)
        {
            string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            try { File.WriteAllText(probe, "ok"); File.Delete(probe); }
            catch { throw new IOException("U盘目录不可写，请关闭写保护并确认有足够空间。位置：" + dir); }
        }

        public void Save()
        {
            Directory.CreateDirectory(dataDir);
            byte[] bytes = Encrypt(new JavaScriptSerializer().Serialize(Data), password);
            string temp = filePath + ".tmp";
            string backup = filePath + ".previous";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (!File.Exists(filePath)) File.Move(temp, filePath);
            else
            {
                try { File.Replace(temp, filePath, backup, true); }
                catch (PlatformNotSupportedException) { File.Copy(filePath, backup, true); File.Copy(temp, filePath, true); File.Delete(temp); }
                catch (IOException) { File.Copy(filePath, backup, true); File.Copy(temp, filePath, true); File.Delete(temp); }
            }
        }

        public void ChangePassword(string next)
        {
            if (next == null || next.Length < 8) throw new ArgumentException("密码至少需要 8 个字符。");
            string old = password;
            password = next;
            try { Save(); }
            catch { password = old; throw; }
        }

        public void Reload() { Data = Read(filePath, password); }

        public bool HasDraft { get { return File.Exists(Path.Combine(dataDir, "sale-draft.dat")); } }

        public void SaveDraft(SaleDraft draft)
        {
            Directory.CreateDirectory(dataDir);
            string path = Path.Combine(dataDir, "sale-draft.dat"), temp = path + ".tmp";
            byte[] bytes = Encrypt(new JavaScriptSerializer().Serialize(draft), password);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (!File.Exists(path)) File.Move(temp, path);
            else try { File.Replace(temp, path, null, true); }
            catch (IOException) { File.Copy(temp, path, true); File.Delete(temp); }
            catch (PlatformNotSupportedException) { File.Copy(temp, path, true); File.Delete(temp); }
        }

        public SaleDraft LoadDraft()
        {
            string path = Path.Combine(dataDir, "sale-draft.dat");
            if (!File.Exists(path)) return null;
            return new JavaScriptSerializer().Deserialize<SaleDraft>(Decrypt(File.ReadAllBytes(path), password));
        }

        public void DeleteDraft()
        {
            string path = Path.Combine(dataDir, "sale-draft.dat");
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }

        public string LatestBackup()
        {
            if (!Directory.Exists(dataDir)) return null;
            return Directory.GetFiles(dataDir, "*.szbbackup", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }

        public long Balance(string customerId)
        {
            var c = Data.Customers.First(x => x.Id == customerId);
            return c.OpeningCents + Data.Entries.Where(x => !x.Deleted && x.CustomerId == customerId)
                .Sum(x => x.Kind == "sale" ? x.AmountCents : -x.AmountCents);
        }

        public void Audit(string action, string type, string id, string reason, object before, object after)
        {
            var json = new JavaScriptSerializer();
            Data.Audit.Add(new AuditRecord {
                Action = action, EntityType = type, EntityId = id, Reason = reason ?? "",
                Before = before == null ? "" : json.Serialize(before), After = after == null ? "" : json.Serialize(after)
            });
        }

        public string Backup(string destination)
        {
            Data.LastBackupUtc = DateTime.UtcNow.ToString("o");
            Save();
            File.Copy(filePath, destination, true);
            return destination;
        }

        public void Restore(string source, string sourcePassword)
        {
            string safety = Path.Combine(dataDir, "恢复前备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".szbbackup");
            Backup(safety);
            LedgerData restored = Read(source, sourcePassword);
            Data = restored;
            Audit("恢复", "账本", "ledger", "从备份恢复", null, null);
            Save();
        }

        public static LedgerData Read(string path, string password)
        {
            int ignored;
            return Read(path, password, out ignored);
        }

        static LedgerData Read(string path, string password, out int oldVersion)
        {
            string json = Decrypt(File.ReadAllBytes(path), password);
            var data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<LedgerData>(json);
            if (data == null || data.SchemaVersion < 1 || data.SchemaVersion > 2) throw new InvalidDataException("不支持的账本格式。");
            oldVersion = data.SchemaVersion;
            Migrate(data);
            return data;
        }

        internal static bool Migrate(LedgerData data)
        {
            bool changed = data.SchemaVersion < 2;
            if (data.Customers == null) data.Customers = new List<Customer>();
            if (data.Products == null) data.Products = new List<Product>();
            if (data.Entries == null) data.Entries = new List<LedgerEntry>();
            if (data.Audit == null) data.Audit = new List<AuditRecord>();
            foreach (var product in data.Products)
            {
                if (product.Name == null) product.Name = "";
                if (product.ShortName == null) product.ShortName = "";
                if (string.IsNullOrWhiteSpace(product.Unit)) product.Unit = "个";
            }
            foreach (var entry in data.Entries)
            {
                if (entry.Date != null && entry.Date.Length == 10) entry.Date += " 00:00";
                if (entry.Details == null) entry.Details = "";
                if (entry.Note == null) entry.Note = "";
                if (entry.Items == null) entry.Items = new List<SaleItem>();
                foreach (var item in entry.Items)
                {
                    if (item.FullName == null) item.FullName = "";
                    if (item.ShortName == null) item.ShortName = "";
                    if (string.IsNullOrWhiteSpace(item.Unit)) item.Unit = "个";
                    if (item.Note == null) item.Note = "";
                }
            }
            data.SchemaVersion = 2;
            return changed;
        }

        public static byte[] Encrypt(string plain, string password)
        {
            byte[] salt = RandomBytes(16), iv = RandomBytes(16), derived;
            using (var kdf = new Rfc2898DeriveBytes(password, salt, Iterations)) derived = kdf.GetBytes(64);
            byte[] cipher;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.Key = derived.Take(32).ToArray(); aes.IV = iv;
                using (var transform = aes.CreateEncryptor()) cipher = transform.TransformFinalBlock(Encoding.UTF8.GetBytes(plain), 0, Encoding.UTF8.GetByteCount(plain));
            }
            byte[] signed = Magic.Concat(salt).Concat(iv).Concat(cipher).ToArray();
            byte[] mac;
            using (var h = new HMACSHA256(derived.Skip(32).Take(32).ToArray())) mac = h.ComputeHash(signed);
            Array.Clear(derived, 0, derived.Length);
            return signed.Concat(mac).ToArray();
        }

        public static string Decrypt(byte[] packed, string password)
        {
            if (packed.Length < 68 || !packed.Take(4).SequenceEqual(Magic)) throw new CryptographicException();
            byte[] salt = packed.Skip(4).Take(16).ToArray(), iv = packed.Skip(20).Take(16).ToArray();
            byte[] cipher = packed.Skip(36).Take(packed.Length - 68).ToArray(), supplied = packed.Skip(packed.Length - 32).ToArray();
            byte[] derived;
            using (var kdf = new Rfc2898DeriveBytes(password, salt, Iterations)) derived = kdf.GetBytes(64);
            byte[] signed = packed.Take(packed.Length - 32).ToArray(), expected;
            using (var h = new HMACSHA256(derived.Skip(32).Take(32).ToArray())) expected = h.ComputeHash(signed);
            if (!FixedEquals(expected, supplied)) { Array.Clear(derived, 0, derived.Length); throw new CryptographicException(); }
            try
            {
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                    aes.Key = derived.Take(32).ToArray(); aes.IV = iv;
                    using (var transform = aes.CreateDecryptor())
                        return Encoding.UTF8.GetString(transform.TransformFinalBlock(cipher, 0, cipher.Length));
                }
            }
            finally { Array.Clear(derived, 0, derived.Length); }
        }

        static byte[] RandomBytes(int size) { var b = new byte[size]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return b; }
        static bool FixedEquals(byte[] a, byte[] b) { int diff = a.Length ^ b.Length; for (int i = 0; i < Math.Min(a.Length, b.Length); i++) diff |= a[i] ^ b[i]; return diff == 0; }
    }

    public sealed class PasswordForm : Form
    {
        readonly TextBox password = new TextBox { UseSystemPasswordChar = true, Width = 250 };
        readonly TextBox confirm = new TextBox { UseSystemPasswordChar = true, Width = 250 };
        readonly bool create;
        public string PasswordValue { get { return password.Text; } }

        public PasswordForm(bool create)
        {
            this.create = create;
            Text = create ? "创建账本密码" : "打开随身赊账本";
            Font = new Font("Microsoft YaHei UI", 12F); AutoScaleMode = AutoScaleMode.Dpi;
            Width = 520; Height = create ? 310 : 245; StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = create ? 4 : 3 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.Controls.Add(new Label { Text = "密码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0); table.Controls.Add(password, 1, 0);
            int buttonRow = 1;
            if (create)
            {
                table.Controls.Add(new Label { Text = "确认密码", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1); table.Controls.Add(confirm, 1, 1);
                table.Controls.Add(new Label { Text = "至少 8 个字符。密码遗失后无法恢复账本。", AutoSize = true, ForeColor = Color.Firebrick }, 1, 2);
                buttonRow = 3;
            }
            var ok = new Button { Text = create ? "创建并进入" : "打开账本", AutoSize = true };
            var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel); table.Controls.Add(buttons, 1, buttonRow);
            ok.Click += delegate { if (ValidatePassword()) { DialogResult = DialogResult.OK; Close(); } };
            Controls.Add(table); AcceptButton = ok; CancelButton = cancel;
        }

        bool ValidatePassword()
        {
            if (password.Text.Length < (create ? 8 : 1)) { MessageBox.Show(create ? "密码至少需要 8 个字符。" : "请输入密码。"); return false; }
            if (create && password.Text != confirm.Text) { MessageBox.Show("两次输入的密码不一致。"); return false; }
            return true;
        }
    }

    public sealed class MainForm : Form
    {
        readonly LedgerStore store;
        readonly Label summary = new Label { AutoSize = true, Font = new Font("Microsoft YaHei UI", 15, FontStyle.Bold), Padding = new Padding(12, 12, 0, 8) };
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        readonly Panel navigation = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Color.FromArgb(248, 249, 251), Padding = new Padding(8, 6, 8, 6) };
        readonly DataGridView customerGrid = Grid();
        readonly DataGridView productGrid = Grid();
        readonly DataGridView statementGrid = Grid();
        readonly DataGridView saleGrid = new DataGridView();
        readonly ComboBox saleCustomer = SearchCombo(), paymentCustomer = SearchCombo(), statementCustomer = SearchCombo();
        readonly DateTimePicker saleDate = MinutePicker(), paymentDate = MinutePicker(), fromDate = DatePicker(), toDate = DatePicker();
        readonly TextBox paymentAmount = Box(), paymentNote = Box(), paymentMethod = Box();
        readonly Label saleTotal = new Label { AutoSize = true, Font = new Font("Microsoft YaHei UI", 16, FontStyle.Bold), ForeColor = Color.FromArgb(20, 90, 55) };
        readonly Label saleMode = new Label { AutoSize = true, Padding = new Padding(10, 12, 0, 0), ForeColor = Color.Firebrick };
        readonly Button saleSaveButton = new Button();
        readonly Button saleExportTableButton = new Button();
        readonly Button saleExportPdfButton = new Button();
        readonly Label statementBalance = new Label { AutoSize = true, Font = new Font("Microsoft YaHei UI", 13, FontStyle.Bold) };
        readonly TextBox customerSearch = new TextBox { Width = 220 };
        readonly TextBox productSearch = new TextBox { Width = 240 };
        readonly Button productDirection = new Button();
        readonly ComboBox statementSort = new ComboBox { Width = 105, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Button statementDirection = new Button();
        List<StatementRow> currentStatement = new List<StatementRow>();
        Customer currentStatementCustomer;
        long currentStatementOpening;
        long currentStatementClosing;
        long currentStatementSales;
        long currentStatementPayments;
        bool statementLoaded;
        bool sortDescending = true;
        bool productSortAscending = true;
        LedgerEntry editingSale;
        readonly Stack<string> saleUndo = new Stack<string>();
        string editSnapshot;
        bool changingSaleGrid;
        bool loadingSale;
        bool saleDirty;
        readonly System.Windows.Forms.Timer saleDraftTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly bool draftEnabled;

        public MainForm(LedgerStore store, bool suppressBackupReminder = false)
        {
            this.store = store;
            draftEnabled = !suppressBackupReminder;
            Text = "随身赊账本"; Width = 1500; Height = 900; MinimumSize = new Size(1100, 700); StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 12F); AutoScaleMode = AutoScaleMode.Dpi; Icon = SystemIcons.Application;
            if (!suppressBackupReminder) WindowState = FormWindowState.Maximized;
            var top = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.FromArgb(245, 247, 250) };
            top.Controls.Add(summary);
            var backup = Button("备份", delegate { Backup(); }); backup.Dock = DockStyle.Right; backup.Width = 90;
            var restore = Button("恢复", delegate { Restore(); }); restore.Dock = DockStyle.Right; restore.Width = 90;
            var update = Button("检查更新", delegate { CheckUpdate(); }); update.Dock = DockStyle.Right; update.Width = 110;
            top.Controls.Add(update); top.Controls.Add(restore); top.Controls.Add(backup);
            BuildCustomers(); BuildSale(); BuildStatement(); BuildProducts(); BuildPayment(); BuildSettings();
            BuildNavigation(); Controls.Add(tabs); Controls.Add(navigation); Controls.Add(top);
            RefreshAll();
            saleDraftTimer.Tick += delegate { saleDraftTimer.Stop(); SaveSaleDraft(false); };
            if (!suppressBackupReminder) { FormClosing += ConfirmClosing; Shown += delegate { OfferDraftRecovery(); }; }
        }

        void BuildNavigation()
        {
            tabs.Appearance = TabAppearance.FlatButtons; tabs.SizeMode = TabSizeMode.Fixed; tabs.ItemSize = new Size(0, 1);
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 250, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
            foreach (string name in new[] { "客户", "销售清单", "查账单", "商品" }) left.Controls.Add(NavigationButton(name));
            right.Controls.Add(NavigationButton("设置")); right.Controls.Add(NavigationButton("记还款"));
            navigation.Controls.Add(left); navigation.Controls.Add(right);
            tabs.SelectedIndexChanged += delegate { UpdateNavigation(); }; UpdateNavigation();
        }

        Button NavigationButton(string name)
        {
            var button = Button(name, delegate { var page = tabs.TabPages.Cast<TabPage>().FirstOrDefault(x => x.Text == name); if (page != null) tabs.SelectedTab = page; });
            button.Tag = name; button.Height = 38; button.MinimumSize = new Size(name.Length > 3 ? 108 : 86, 38); button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0;
            return button;
        }

        void UpdateNavigation()
        {
            foreach (Button button in navigation.Controls.Cast<Control>().SelectMany(x => x.Controls.Cast<Control>()).OfType<Button>())
            { bool selected = tabs.SelectedTab != null && Convert.ToString(button.Tag) == tabs.SelectedTab.Text; button.BackColor = selected ? Color.FromArgb(220, 232, 224) : Color.Transparent; }
        }

        void BuildCustomers()
        {
            var tab = new TabPage("客户");
            var bar = Bar();
            bar.Controls.Add(Button("新增客户", delegate { EditCustomer(null); }));
            bar.Controls.Add(Button("修改", delegate { var c = Selected<Customer>(customerGrid); if (c != null) EditCustomer(c); }));
            bar.Controls.Add(Button("停用/启用", delegate { ToggleCustomer(); }));
            bar.Controls.Add(new Label { Text = "搜索", AutoSize = true, Padding = new Padding(12, 8, 0, 0) });
            bar.Controls.Add(customerSearch);
            customerSearch.TextChanged += delegate { RefreshCustomers(); };
            tab.Controls.Add(customerGrid); tab.Controls.Add(bar); tabs.TabPages.Add(tab);
        }

        void BuildSale()
        {
            var tab = new TabPage("销售清单");
            var header = Bar();
            header.Controls.Add(new Label { Text = "客户", AutoSize = true, Padding = new Padding(0, 10, 0, 0) });
            saleCustomer.Width = 310; header.Controls.Add(saleCustomer);
            header.Controls.Add(new Label { Text = "销售时间", AutoSize = true, Padding = new Padding(14, 10, 0, 0) });
            header.Controls.Add(saleDate); header.Controls.Add(saleMode);

            ConfigureSaleGrid();

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 72, Padding = new Padding(8), BackColor = Color.FromArgb(248, 249, 251) };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false };
            actions.Controls.Add(Button("新销售单", delegate { NewSale(); }));
            actions.Controls.Add(Button("新建行", delegate { AddSaleRow(); }));
            actions.Controls.Add(Button("删除选中行", delegate { DeleteSaleRow(); }));
            actions.Controls.Add(Button("撤回上一步", delegate { UndoSale(); }));
            saleSaveButton.Text = "上传到账单"; saleSaveButton.AutoSize = true; saleSaveButton.Padding = new Padding(12, 5, 12, 5); saleSaveButton.Margin = new Padding(12, 4, 4, 4);
            saleSaveButton.Click += delegate { UploadSaleToLedger(); }; actions.Controls.Add(saleSaveButton);
            ConfigureActionButton(saleExportTableButton, "导出表格", delegate { ExportSaleXlsx(); }); actions.Controls.Add(saleExportTableButton);
            ConfigureActionButton(saleExportPdfButton, "导出PDF", delegate { ExportSalePdf(); }); actions.Controls.Add(saleExportPdfButton);
            var totalPanel = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            totalPanel.Controls.Add(new Label { Text = "销售单总金额", AutoSize = true, Padding = new Padding(0, 11, 6, 0) });
            saleTotal.Text = "¥0.00"; totalPanel.Controls.Add(saleTotal);
            footer.Controls.Add(totalPanel); footer.Controls.Add(actions);

            tab.Controls.Add(saleGrid); tab.Controls.Add(footer); tab.Controls.Add(header); tabs.TabPages.Add(tab);
            saleCustomer.SelectedIndexChanged += delegate { MarkSaleDirty(); };
            saleCustomer.TextChanged += delegate { MarkSaleDirty(); };
            saleDate.ValueChanged += delegate { MarkSaleDirty(); };
            UpdateSaleState();
        }

        static void ConfigureActionButton(Button button, string text, EventHandler click)
        {
            button.Text = text; button.AutoSize = true; button.Padding = new Padding(8, 3, 8, 3); button.Margin = new Padding(4);
            button.Click += click;
        }

        void BuildPayment()
        {
            var tab = new TabPage("记还款");
            var form = FormTable(); int row = 0;
            AddRow(form, row++, "客户", paymentCustomer);
            AddRow(form, row++, "日期", paymentDate);
            AddRow(form, row++, "金额（元）", paymentAmount);
            paymentMethod.Text = "现金"; AddRow(form, row++, "方式", paymentMethod);
            AddRow(form, row++, "备注", paymentNote);
            var save = Button("保存还款记录", delegate { SavePayment(); }); save.Height = 38; AddRow(form, row++, "", save);
            tab.Controls.Add(form); tabs.TabPages.Add(tab);
        }

        void BuildStatement()
        {
            var tab = new TabPage("查账单");
            var controls = new TableLayoutPanel { Dock = DockStyle.Top, Height = 126, ColumnCount = 1, RowCount = 2, Padding = new Padding(6) };
            controls.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); controls.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            filters.Controls.Add(new Label { Text = "客户", AutoSize = true, Padding = new Padding(0, 10, 0, 0) }); statementCustomer.Width = 280; filters.Controls.Add(statementCustomer);
            filters.Controls.Add(new Label { Text = "从", AutoSize = true, Padding = new Padding(10, 10, 0, 0) }); filters.Controls.Add(fromDate);
            filters.Controls.Add(new Label { Text = "到", AutoSize = true, Padding = new Padding(10, 10, 0, 0) }); filters.Controls.Add(toDate);
            filters.Controls.Add(Button("查询", delegate { LoadStatement(); }));
            filters.Controls.Add(new Label { Text = "排序", AutoSize = true, Padding = new Padding(12, 10, 0, 0) });
            statementSort.Items.AddRange(new object[] { "日期", "客户" }); statementSort.SelectedIndex = 0; filters.Controls.Add(statementSort);
            statementDirection.Text = "新 → 旧"; statementDirection.AutoSize = true; statementDirection.Padding = new Padding(8, 3, 8, 3); filters.Controls.Add(statementDirection);
            statementSort.SelectedIndexChanged += delegate { sortDescending = statementSort.Text == "日期"; UpdateSortButton(); if (statementLoaded) LoadStatement(); };
            statementDirection.Click += delegate { sortDescending = !sortDescending; UpdateSortButton(); if (statementLoaded) LoadStatement(); };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            actions.Controls.Add(Button("修改选中", delegate { EditSelectedEntry(); }));
            actions.Controls.Add(Button("删除选中", delegate { DeleteSelectedEntry(); }));
            actions.Controls.Add(Button("导出表格", delegate { ExportStatementXlsx(); }));
            actions.Controls.Add(Button("打印/PDF", delegate { PrintStatement(); }));
            controls.Controls.Add(filters, 0, 0); controls.Controls.Add(actions, 0, 1);
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58 }; statementBalance.Padding = new Padding(12, 14, 0, 0); bottom.Controls.Add(statementBalance);
            tab.Controls.Add(statementGrid); tab.Controls.Add(bottom); tab.Controls.Add(controls); tabs.TabPages.Add(tab);
            fromDate.Value = DateTime.Today.AddMonths(-1);
        }

        void BuildProducts()
        {
            var tab = new TabPage("商品"); var bar = Bar();
            bar.Controls.Add(Button("新增商品", delegate { EditProduct(null); }));
            bar.Controls.Add(Button("修改", delegate { var p = Selected<Product>(productGrid); if (p != null) EditProduct(p); }));
            bar.Controls.Add(Button("停用/启用", delegate { ToggleProduct(); }));
            bar.Controls.Add(new Label { Text = "搜索", AutoSize = true, Padding = new Padding(12, 8, 0, 0) }); bar.Controls.Add(productSearch);
            productDirection.Text = "商品全名 A → Z"; productDirection.AutoSize = true; productDirection.Padding = new Padding(8, 3, 8, 3); productDirection.Margin = new Padding(12, 4, 4, 4); bar.Controls.Add(productDirection);
            productSearch.TextChanged += delegate { RefreshProducts(); };
            productDirection.Click += delegate { productSortAscending = !productSortAscending; productDirection.Text = productSortAscending ? "商品全名 A → Z" : "商品全名 Z → A"; RefreshProducts(); };
            tab.Controls.Add(productGrid); tab.Controls.Add(bar); tabs.TabPages.Add(tab);
        }

        void BuildSettings()
        {
            var tab = new TabPage("设置"); var form = FormTable(); int row = 0;
            TextBox shop = Box(), phone = Box(), address = Box(), repo = Box();
            shop.Text = store.Data.ShopName; phone.Text = store.Data.ShopPhone; address.Text = store.Data.ShopAddress; repo.Text = store.Data.GitHubRepository;
            AddRow(form, row++, "店名", shop); AddRow(form, row++, "联系电话", phone); AddRow(form, row++, "地址", address);
            AddRow(form, row++, "GitHub仓库", repo);
            var hint = new Label { AutoSize = true, Text = "格式：owner/repository。仅用于检查更新，不上传账本数据。", ForeColor = Color.DimGray };
            AddRow(form, row++, "", hint);
            AddRow(form, row++, "", Button("保存设置", delegate { store.Data.ShopName = shop.Text.Trim(); store.Data.ShopPhone = phone.Text.Trim(); store.Data.ShopAddress = address.Text.Trim(); store.Data.GitHubRepository = repo.Text.Trim(); SaveAndRefresh(); }));
            AddRow(form, row++, "", Button("修改账本密码", delegate { ChangePassword(); }));
            var audit = new Label { AutoSize = true, Text = "当前版本：" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + "    审计记录：" + store.Data.Audit.Count, ForeColor = Color.DimGray };
            AddRow(form, row++, "", audit);
            tab.Controls.Add(form); tabs.TabPages.Add(tab);
        }

        void EditCustomer(Customer existing)
        {
            var original = existing == null ? null : Clone(existing);
            using (var d = new RecordDialog(existing == null ? "新增客户" : "修改客户",
                new[] { "姓名", "手机号", "地址", "备注", "期初欠款（元）", "期初日期" },
                existing == null ? new[] { "", "", "", "", "0.00", DateTime.Today.ToString("yyyy-MM-dd") } :
                new[] { existing.Name, existing.Phone, existing.Address, existing.Note, Money.Text(existing.OpeningCents), existing.OpeningDate }))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (d.Values[0].Trim().Length == 0) { MessageBox.Show("姓名不能为空。"); return; }
                DateTime openingDate; if (!DateTime.TryParse(d.Values[5], out openingDate)) { MessageBox.Show("期初日期格式不正确。"); return; }
                long openingCents; try { openingCents = Money.Parse(d.Values[4]); } catch (Exception ex) { MessageBox.Show(ex.Message); return; }
                string nextName = d.Values[0].Trim(), nextPhone = d.Values[1].Trim();
                bool duplicate = store.Data.Customers.Any(x => x.Id != (existing == null ? "" : existing.Id) && x.Name == nextName && x.Phone == nextPhone);
                if (duplicate && MessageBox.Show("已有姓名和手机号相同的客户，仍要保存吗？", "可能重复", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                Customer c = existing ?? new Customer();
                c.Name = nextName; c.Phone = nextPhone; c.Address = d.Values[2].Trim(); c.Note = d.Values[3].Trim();
                c.OpeningCents = openingCents;
                c.OpeningDate = openingDate.ToString("yyyy-MM-dd");
                if (existing == null) store.Data.Customers.Add(c);
                store.Audit(existing == null ? "新增" : "修改", "客户", c.Id, existing == null ? "新建客户" : "用户修改", original, c);
                SaveAndRefresh();
            }
        }

        void ToggleCustomer()
        {
            var c = Selected<Customer>(customerGrid); if (c == null) return;
            c.Active = !c.Active; store.Audit(c.Active ? "启用" : "停用", "客户", c.Id, "用户操作", null, c); SaveAndRefresh();
        }

        void EditProduct(Product existing)
        {
            var original = existing == null ? null : Clone(existing);
            using (var d = new ProductDialog(existing))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                long priceCents; try { priceCents = Money.Parse(d.PriceText); } catch (Exception ex) { MessageBox.Show(ex.Message); return; }
                if (d.FullName.Trim().Length == 0) { MessageBox.Show("商品全名不能为空。"); return; }
                if (priceCents <= 0) { MessageBox.Show("默认单价必须大于零。"); return; }
                if (store.Data.Products.Any(x => x.Id != (existing == null ? "" : existing.Id) && string.Equals(x.Name, d.FullName.Trim(), StringComparison.CurrentCultureIgnoreCase))) { MessageBox.Show("商品全名已经存在，请直接修改原商品。"); return; }
                Product p = existing ?? new Product(); p.Name = d.FullName.Trim(); p.ShortName = d.ShortName.Trim(); p.Unit = d.Unit.Trim(); p.PriceCents = priceCents;
                if (existing == null) store.Data.Products.Add(p);
                store.Audit(existing == null ? "新增" : "修改", "商品", p.Id, existing == null ? "新建商品" : "用户修改", original, p);
                SaveAndRefresh();
            }
        }

        void ToggleProduct()
        {
            var p = Selected<Product>(productGrid); if (p == null) return;
            p.Active = !p.Active; store.Audit(p.Active ? "启用" : "停用", "商品", p.Id, "用户操作", null, p); SaveAndRefresh();
        }

        void ConfigureSaleGrid()
        {
            saleGrid.Dock = DockStyle.Fill; saleGrid.AllowUserToAddRows = false; saleGrid.AllowUserToDeleteRows = false;
            saleGrid.RowHeadersVisible = false; saleGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; saleGrid.MultiSelect = false;
            saleGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None; saleGrid.BackgroundColor = Color.White;
            saleGrid.BorderStyle = BorderStyle.None; saleGrid.EditMode = DataGridViewEditMode.EditOnEnter;
            saleGrid.RowTemplate.Height = 38; saleGrid.ColumnHeadersHeight = 42;
            saleGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sequence", HeaderText = "序号", Width = 60, ReadOnly = true });
            saleGrid.Columns.Add(new DataGridViewComboBoxColumn { Name = "FullName", HeaderText = "商品全名", Width = 250, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox, FlatStyle = FlatStyle.Flat });
            saleGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ShortName", HeaderText = "商品名称", Width = 180 });
            saleGrid.Columns.Add(new DataGridViewComboBoxColumn { Name = "Unit", HeaderText = "单位", Width = 80, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox, FlatStyle = FlatStyle.Flat });
            saleGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Quantity", HeaderText = "数量", Width = 85 });
            saleGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PieceCount", HeaderText = "件数", Width = 85 });
            saleGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Price", HeaderText = "单价", Width = 110 });
            saleGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Amount", HeaderText = "金额", Width = 120 });
            saleGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Note", HeaderText = "备注", Width = 230 });
            RefreshSaleChoices();
            saleGrid.EditingControlShowing += SaleGridEditingControlShowing;
            saleGrid.CellBeginEdit += delegate { if (!changingSaleGrid) editSnapshot = SaleSnapshot(); };
            saleGrid.CellEndEdit += delegate(object sender, DataGridViewCellEventArgs e) { FinishSaleCellEdit(e.RowIndex, e.ColumnIndex); };
            saleGrid.CellValidating += delegate(object sender, DataGridViewCellValidatingEventArgs e)
            {
                if (saleGrid.Columns[e.ColumnIndex].Name == "FullName")
                {
                    string value = Convert.ToString(e.FormattedValue).Trim();
                    var column = (DataGridViewComboBoxColumn)saleGrid.Columns["FullName"];
                    if (value.Length > 0 && !column.Items.Cast<object>().Any(x => string.Equals(Convert.ToString(x), value, StringComparison.CurrentCultureIgnoreCase))) column.Items.Add(value);
                }
            };
            saleGrid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
        }

        void RefreshSaleChoices()
        {
            var productColumn = saleGrid.Columns["FullName"] as DataGridViewComboBoxColumn;
            var unitColumn = saleGrid.Columns["Unit"] as DataGridViewComboBoxColumn;
            if (productColumn == null || unitColumn == null) return;
            var draftNames = saleGrid.Rows.Cast<DataGridViewRow>().Select(x => Cell(x, "FullName")).Where(x => x.Length > 0).ToList();
            var draftUnits = saleGrid.Rows.Cast<DataGridViewRow>().Select(x => Cell(x, "Unit")).Where(x => x.Length > 0).ToList();
            productColumn.Items.Clear();
            foreach (var name in store.Data.Products.Where(x => x.Active).Select(x => x.Name).Where(x => x.Length > 0).Distinct().OrderBy(x => x)) productColumn.Items.Add(name);
            foreach (var name in draftNames) if (!productColumn.Items.Contains(name)) productColumn.Items.Add(name);
            unitColumn.Items.Clear(); unitColumn.Items.Add("个"); unitColumn.Items.Add("件");
            foreach (var unit in store.Data.Products.Select(x => x.Unit).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()) if (!unitColumn.Items.Contains(unit)) unitColumn.Items.Add(unit);
            foreach (var unit in draftUnits) if (!unitColumn.Items.Contains(unit)) unitColumn.Items.Add(unit);
        }

        void SaleGridEditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            var combo = e.Control as ComboBox;
            if (combo == null) return;
            combo.DropDownStyle = ComboBoxStyle.DropDown;
            combo.AutoCompleteSource = AutoCompleteSource.ListItems;
            combo.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        }

        void AddSaleRow()
        {
            saleGrid.EndEdit(); PushSaleUndo(SaleSnapshot());
            changingSaleGrid = true;
            int index = saleGrid.Rows.Add(saleGrid.Rows.Count + 1, "", "", "个", "", "", "", "", "");
            changingSaleGrid = false; saleGrid.CurrentCell = saleGrid.Rows[index].Cells["FullName"]; saleGrid.BeginEdit(true); UpdateSaleTotal();
            MarkSaleDirty();
        }

        void DeleteSaleRow()
        {
            if (saleGrid.CurrentRow == null) { MessageBox.Show("请选择要删除的商品行。"); return; }
            saleGrid.EndEdit(); PushSaleUndo(SaleSnapshot());
            changingSaleGrid = true; saleGrid.Rows.RemoveAt(saleGrid.CurrentRow.Index); RenumberSaleRows(); changingSaleGrid = false; UpdateSaleTotal();
            MarkSaleDirty();
        }

        void UndoSale()
        {
            saleGrid.EndEdit();
            if (saleUndo.Count == 0) { MessageBox.Show("当前销售清单没有可撤回的操作。"); return; }
            RestoreSaleSnapshot(saleUndo.Pop());
        }

        void FinishSaleCellEdit(int rowIndex, int columnIndex)
        {
            if (changingSaleGrid || rowIndex < 0 || columnIndex < 0) return;
            changingSaleGrid = true;
            string column = saleGrid.Columns[columnIndex].Name;
            var row = saleGrid.Rows[rowIndex];
            if (column == "FullName")
            {
                string name = Cell(row, "FullName");
                var product = store.Data.Products.FirstOrDefault(x => x.Active && string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase));
                if (product != null)
                {
                    row.Cells["FullName"].Value = product.Name; row.Cells["ShortName"].Value = product.ShortName;
                    EnsureComboValue("Unit", product.Unit); row.Cells["Unit"].Value = product.Unit; row.Cells["Price"].Value = Money.Text(product.PriceCents);
                }
            }
            if (column == "Quantity" && Cell(row, "Quantity").Length > 0) row.Cells["PieceCount"].Value = "";
            if (column == "PieceCount" && Cell(row, "PieceCount").Length > 0) row.Cells["Quantity"].Value = "";
            if (column == "Amount")
            {
                try
                {
                    long calculated = SaleAmount(Cell(row, "Quantity"), Cell(row, "PieceCount"), Cell(row, "Price"));
                    long entered = SaleItemAmount(Cell(row, "Quantity"), Cell(row, "PieceCount"), Cell(row, "Price"), Cell(row, "Amount"));
                    row.Cells["Amount"].Value = Money.Text(entered);
                    if (entered != calculated) MessageBox.Show("金额已手动修改，与数量乘以单价的自动金额不同。上传到账单时将使用手动金额。", "金额已修改", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "金额不正确", MessageBoxButtons.OK, MessageBoxIcon.Warning); CalculateSaleRow(row); }
            }
            else CalculateSaleRow(row);
            changingSaleGrid = false;
            string after = SaleSnapshot(); if (editSnapshot != null && editSnapshot != after) PushSaleUndo(editSnapshot); editSnapshot = null;
            MarkSaleDirty(); UpdateSaleTotal();
        }

        void EnsureComboValue(string columnName, string value)
        {
            var column = saleGrid.Columns[columnName] as DataGridViewComboBoxColumn;
            if (column != null && value.Length > 0 && !column.Items.Contains(value)) column.Items.Add(value);
        }

        void CalculateSaleRow(DataGridViewRow row)
        {
            try { row.Cells["Amount"].Value = Money.Text(SaleAmount(Cell(row, "Quantity"), Cell(row, "PieceCount"), Cell(row, "Price"))); }
            catch { row.Cells["Amount"].Value = ""; }
        }

        internal static long SaleAmount(string quantityText, string pieceText, string priceText)
        {
            int quantity, pieces;
            bool quantityValid = int.TryParse(quantityText, out quantity) && quantity > 0;
            bool piecesValid = int.TryParse(pieceText, out pieces) && pieces > 0;
            if (quantityValid == piecesValid) throw new FormatException("数量和件数必须二选一，并填写大于零的整数。");
            long price = Money.Parse(priceText); if (price <= 0) throw new FormatException("单价必须大于零。");
            return checked(price * (quantityValid ? quantity : pieces));
        }

        internal static long SaleItemAmount(string quantityText, string pieceText, string priceText, string amountText)
        {
            long calculated = SaleAmount(quantityText, pieceText, priceText);
            if (string.IsNullOrWhiteSpace(amountText)) return calculated;
            long amount = Money.Parse(amountText); if (amount <= 0) throw new FormatException("金额必须大于零。");
            return amount;
        }

        List<SaleItem> ReadSaleItems(bool validate)
        {
            var items = new List<SaleItem>();
            for (int i = 0; i < saleGrid.Rows.Count; i++)
            {
                var row = saleGrid.Rows[i]; string fullName = Cell(row, "FullName");
                if (!validate && fullName.Length == 0 && Cell(row, "Price").Length == 0) continue;
                if (fullName.Length == 0) throw new SaleValidationException("第 " + (i + 1) + " 行：商品全名不能为空。", i, saleGrid.Columns["FullName"].Index);
                int quantity, pieces; int.TryParse(Cell(row, "Quantity"), out quantity); int.TryParse(Cell(row, "PieceCount"), out pieces);
                bool q = quantity > 0, p = pieces > 0; long price, amount;
                try { price = Money.Parse(Cell(row, "Price")); SaleAmount(Cell(row, "Quantity"), Cell(row, "PieceCount"), Cell(row, "Price")); }
                catch (OverflowException) { throw new SaleValidationException("第 " + (i + 1) + " 行：金额过大。", i, saleGrid.Columns["Price"].Index); }
                catch (Exception ex) { throw new SaleValidationException("第 " + (i + 1) + " 行：" + ex.Message, i, q == p ? saleGrid.Columns["Quantity"].Index : saleGrid.Columns["Price"].Index); }
                try { amount = SaleItemAmount(Cell(row, "Quantity"), Cell(row, "PieceCount"), Cell(row, "Price"), Cell(row, "Amount")); }
                catch (Exception ex) { throw new SaleValidationException("第 " + (i + 1) + " 行：" + ex.Message, i, saleGrid.Columns["Amount"].Index); }
                items.Add(new SaleItem { FullName = fullName, ShortName = Cell(row, "ShortName"), Unit = Cell(row, "Unit").Length == 0 ? "个" : Cell(row, "Unit"), Quantity = q ? quantity : 0, PieceCount = p ? pieces : 0, PriceCents = price, AmountCents = amount, Note = Cell(row, "Note") });
            }
            return items;
        }

        string SaleSnapshot()
        {
            var rows = new List<SaleDraftRow>();
            foreach (DataGridViewRow row in saleGrid.Rows) rows.Add(new SaleDraftRow { FullName = Cell(row, "FullName"), ShortName = Cell(row, "ShortName"), Unit = Cell(row, "Unit"), Quantity = Cell(row, "Quantity"), PieceCount = Cell(row, "PieceCount"), Price = Cell(row, "Price"), Amount = Cell(row, "Amount"), Note = Cell(row, "Note") });
            return new JavaScriptSerializer().Serialize(rows);
        }

        void RestoreSaleSnapshot(string snapshot)
        {
            var rows = new JavaScriptSerializer().Deserialize<List<SaleDraftRow>>(snapshot) ?? new List<SaleDraftRow>();
            changingSaleGrid = true; saleGrid.Rows.Clear();
            foreach (var item in rows)
            {
                EnsureComboValue("FullName", item.FullName); EnsureComboValue("Unit", item.Unit);
                saleGrid.Rows.Add(saleGrid.Rows.Count + 1, item.FullName, item.ShortName, item.Unit, item.Quantity, item.PieceCount, item.Price, item.Amount, item.Note);
            }
            changingSaleGrid = false; MarkSaleDirty(); UpdateSaleTotal();
        }

        void PushSaleUndo(string snapshot)
        {
            if (saleUndo.Count == 0 || saleUndo.Peek() != snapshot) saleUndo.Push(snapshot);
        }

        void RenumberSaleRows() { for (int i = 0; i < saleGrid.Rows.Count; i++) saleGrid.Rows[i].Cells["Sequence"].Value = i + 1; }
        static string Cell(DataGridViewRow row, string column) { return Convert.ToString(row.Cells[column].Value).Trim(); }
        static string SaleSummary(List<SaleItem> items) { return string.Join("；", items.Select(x => x.FullName + " × " + (x.Quantity > 0 ? x.Quantity : x.PieceCount) + " " + x.Unit + (x.Note.Length == 0 ? "" : "（" + x.Note + "）")).ToArray()); }

        static void RestoreEntry(LedgerEntry target, LedgerEntry source)
        {
            target.CustomerId = source.CustomerId; target.Kind = source.Kind; target.Date = source.Date; target.Details = source.Details;
            target.AmountCents = source.AmountCents; target.Note = source.Note; target.Items = source.Items; target.Deleted = source.Deleted;
            target.CreatedUtc = source.CreatedUtc; target.ModifiedUtc = source.ModifiedUtc;
        }

        void LoadSaleForEdit(LedgerEntry entry)
        {
            if (saleDirty && MessageBox.Show("当前尚未上传的销售清单将被替换，继续吗？", "载入销售单", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            loadingSale = true;
            try
            {
                editingSale = entry;
                var customer = store.Data.Customers.FirstOrDefault(x => x.Id == entry.CustomerId);
                BindCustomer(saleCustomer, store.Data.Customers.Where(x => x.Active || x.Id == entry.CustomerId).ToList(), customer);
                DateTime when; if (DateTime.TryParse(entry.Date, out when)) saleDate.Value = when;
                changingSaleGrid = true; saleGrid.Rows.Clear();
                foreach (var item in entry.Items)
                {
                    EnsureComboValue("FullName", item.FullName); EnsureComboValue("Unit", item.Unit);
                    saleGrid.Rows.Add(saleGrid.Rows.Count + 1, item.FullName, item.ShortName, item.Unit,
                        item.Quantity > 0 ? item.Quantity.ToString() : "", item.PieceCount > 0 ? item.PieceCount.ToString() : "",
                        Money.Text(item.PriceCents), Money.Text(item.AmountCents), item.Note);
                }
                changingSaleGrid = false; saleUndo.Clear(); UpdateSaleTotal(); saleDirty = false;
            }
            finally { changingSaleGrid = false; loadingSale = false; }
            UpdateSaleState();
            if (draftEnabled) store.DeleteDraft();
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().First(x => x.Text == "销售清单");
        }

        void ClearSaleDraft()
        {
            loadingSale = true; changingSaleGrid = true;
            try { saleGrid.Rows.Clear(); saleUndo.Clear(); editingSale = null; saleDate.Value = DateTime.Now; saleDirty = false; UpdateSaleTotal(); }
            finally { changingSaleGrid = false; loadingSale = false; }
            UpdateSaleState();
            if (draftEnabled) store.DeleteDraft();
        }

        void NewSale()
        {
            if (saleDirty && MessageBox.Show("当前销售清单尚未上传到账单，确定新建吗？", "新销售单", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            ClearSaleDraft();
        }

        void MarkSaleDirty()
        {
            if (loadingSale || changingSaleGrid) return;
            saleDirty = true; UpdateSaleState();
            if (draftEnabled) { saleDraftTimer.Stop(); saleDraftTimer.Start(); }
        }

        bool SaveSaleDraft(bool showError)
        {
            if (!draftEnabled || !saleDirty) return true;
            try
            {
                saleGrid.EndEdit();
                var selected = saleCustomer.SelectedItem as Customer;
                store.SaveDraft(new SaleDraft { CustomerId = selected == null ? "" : selected.Id, CustomerText = saleCustomer.Text.Trim(),
                    Date = saleDate.Value.ToString("yyyy-MM-dd HH:mm"), EditingSaleId = editingSale == null ? "" : editingSale.Id,
                    Rows = new JavaScriptSerializer().Deserialize<List<SaleDraftRow>>(SaleSnapshot()) ?? new List<SaleDraftRow>() });
                return true;
            }
            catch (Exception ex)
            {
                saleMode.Text = "草稿自动保存失败";
                if (showError) MessageBox.Show("未上传销售清单的草稿无法写入U盘：\r\n" + ex.Message, "草稿保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        void OfferDraftRecovery()
        {
            if (!store.HasDraft) return;
            SaleDraft draft;
            try { draft = store.LoadDraft(); }
            catch (Exception ex) { MessageBox.Show("未保存的销售草稿已损坏，正式账本不受影响：\r\n" + ex.Message, "草稿无法恢复", MessageBoxButtons.OK, MessageBoxIcon.Warning); store.DeleteDraft(); return; }
            if (draft == null) return;
            if (MessageBox.Show("发现上次未保存的销售单草稿，是否恢复？", "恢复销售草稿", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { store.DeleteDraft(); return; }
            loadingSale = true;
            try
            {
                editingSale = store.Data.Entries.FirstOrDefault(x => !x.Deleted && x.Id == draft.EditingSaleId);
                var customer = store.Data.Customers.FirstOrDefault(x => x.Id == draft.CustomerId);
                BindCustomer(saleCustomer, store.Data.Customers.Where(x => x.Active || (customer != null && x.Id == customer.Id)).ToList(), customer);
                if (customer == null) saleCustomer.Text = draft.CustomerText ?? "";
                DateTime when; if (DateTime.TryParse(draft.Date, out when)) saleDate.Value = when;
                RestoreSaleSnapshot(new JavaScriptSerializer().Serialize(draft.Rows ?? new List<SaleDraftRow>()));
                saleDirty = true; UpdateSaleState();
            }
            finally { loadingSale = false; }
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().First(x => x.Text == "销售清单");
        }

        void UpdateSaleState()
        {
            bool canExport = editingSale != null && !saleDirty;
            saleExportTableButton.Enabled = canExport; saleExportPdfButton.Enabled = canExport;
            saleSaveButton.Text = editingSale == null ? "上传到账单" : "更新到账单";
            saleMode.Text = canExport ? "已上传，可导出" : (saleDirty ? "有未上传的修改" : "");
        }

        internal void PrepareSmoke(string tabName)
        {
            if (tabName == "销售清单")
            {
                var saved = store.Data.Entries.FirstOrDefault(x => x.Kind == "sale" && !x.Deleted && x.Items != null && x.Items.Count > 0);
                if (saved != null) LoadSaleForEdit(saved);
            }
            if (tabName == "查账单") LoadStatement();
            var tab = tabs.TabPages.Cast<TabPage>().FirstOrDefault(x => x.Text == tabName); if (tab != null) tabs.SelectedTab = tab;
        }

        void UploadSaleToLedger()
        {
            saleGrid.EndEdit();
            var c = saleCustomer.SelectedItem as Customer; if (c == null) { MessageBox.Show("请从列表中选择已有客户。"); saleCustomer.Focus(); return; }
            List<SaleItem> items;
            try { items = ReadSaleItems(true); }
            catch (SaleValidationException ex)
            {
                MessageBox.Show(ex.Message, "销售清单未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (ex.Row >= 0 && ex.Column >= 0) { saleGrid.CurrentCell = saleGrid.Rows[ex.Row].Cells[ex.Column]; saleGrid.BeginEdit(true); }
                return;
            }
            if (items.Count == 0) { MessageBox.Show("请先点击“新建行”并录入商品。"); return; }
            long total = items.Sum(x => x.AmountCents);
            string details = SaleSummary(items);
            LedgerEntry entry = editingSale;
            LedgerEntry before = entry == null ? null : Clone(entry);
            string reason = entry == null ? "上传销售单" : "更新销售单";
            if (entry == null) entry = new LedgerEntry();
            entry.CustomerId = c.Id; entry.Kind = "sale"; entry.Date = saleDate.Value.ToString("yyyy-MM-dd HH:mm");
            entry.Items = items; entry.AmountCents = total; entry.Details = details; entry.Note = "";
            int auditCount = store.Data.Audit.Count;
            var addedProducts = MissingProducts(items, store.Data.Products);
            foreach (var product in addedProducts)
            {
                store.Data.Products.Add(product); store.Audit("新增", "商品", product.Id, "销售单自动添加", null, product);
            }
            if (before == null) store.Data.Entries.Add(entry); else entry.ModifiedUtc = DateTime.UtcNow.ToString("o");
            store.Audit(before == null ? "新增" : "修改", "销售单", entry.Id, reason, before, entry);
            if (!SaveAndRefresh())
            {
                foreach (var product in addedProducts) store.Data.Products.RemoveAll(x => x.Id == product.Id);
                var failedEntry = store.Data.Entries.FirstOrDefault(x => x.Id == entry.Id);
                if (before == null) { if (failedEntry != null) store.Data.Entries.Remove(failedEntry); } else if (failedEntry != null) RestoreEntry(failedEntry, before);
                while (store.Data.Audit.Count > auditCount) store.Data.Audit.RemoveAt(store.Data.Audit.Count - 1);
                return;
            }
            string customerName = c.Name; editingSale = entry; saleDirty = false; saleUndo.Clear(); UpdateSaleState();
            saleDraftTimer.Stop(); if (draftEnabled) store.DeleteDraft();
            MessageBox.Show("销售清单已上传到账单，" + customerName + " 当前" + Money.Label(store.Balance(c.Id)) + "。", "上传成功");
        }

        internal static List<Product> MissingProducts(IEnumerable<SaleItem> items, IEnumerable<Product> existing)
        {
            var names = new HashSet<string>(existing.Select(x => x.Name), StringComparer.CurrentCultureIgnoreCase); var result = new List<Product>();
            foreach (var item in items)
                if (names.Add(item.FullName)) result.Add(new Product { Name = item.FullName, ShortName = item.ShortName, Unit = string.IsNullOrWhiteSpace(item.Unit) ? "个" : item.Unit, PriceCents = item.PriceCents });
            return result;
        }

        SaleExportDocument CurrentSaleExport()
        {
            if (editingSale == null || saleDirty) { MessageBox.Show("请先把当前销售清单上传到账单，再进行导出。"); return null; }
            var customer = store.Data.Customers.FirstOrDefault(x => x.Id == editingSale.CustomerId);
            if (customer == null) { MessageBox.Show("找不到这张销售单对应的客户。"); return null; }
            return SaleExportFormatter.Create(customer, editingSale);
        }

        void ExportSaleXlsx()
        {
            var sale = CurrentSaleExport(); if (sale == null) return;
            var table = SaleExportFormatter.Table(sale);
            using (var save = new SaveFileDialog { Filter = "Excel 工作簿|*.xlsx", FileName = table.FileName + ".xlsx" })
            {
                if (save.ShowDialog(this) != DialogResult.OK) return;
                try { XlsxWriter.Write(save.FileName, table); }
                catch (Exception ex) { MessageBox.Show("表格导出失败：\r\n" + ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                MessageBox.Show("销售清单已导出。\r\n" + save.FileName, "导出成功");
            }
        }

        void ExportSalePdf()
        {
            var sale = CurrentSaleExport(); if (sale == null) return;
            string printer = PrinterSettings.InstalledPrinters.Cast<string>().FirstOrDefault(x => string.Equals(x, "Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase));
            if (printer == null) { MessageBox.Show("未找到 Microsoft Print to PDF。请先在 Windows 的“可选功能”中启用它。", "无法导出 PDF", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            try { PrintTablePdf(SaleExportFormatter.Table(sale), printer, null); }
            catch (Exception ex) { MessageBox.Show("PDF 导出失败：\r\n" + ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        internal static void PrintTablePdf(ExportTable table, string printer, string output)
        {
            int rowIndex = 0;
            using (var doc = new PrintDocument())
            {
                doc.DocumentName = table.FileName + ".pdf";
                if (!string.IsNullOrEmpty(printer)) doc.PrinterSettings.PrinterName = printer;
                doc.DefaultPageSettings.Landscape = true; doc.DefaultPageSettings.PaperSize = new PaperSize("A4", 827, 1169); doc.DefaultPageSettings.Margins = new Margins(24, 24, 28, 28);
                if (output != null) { doc.PrinterSettings.PrintToFile = true; doc.PrinterSettings.PrintFileName = Path.GetFullPath(output); doc.PrintController = new StandardPrintController(); }
                doc.PrintPage += delegate(object sender, PrintPageEventArgs e) { DrawTablePage(e, table, ref rowIndex); };
                doc.Print();
            }
        }

        static void DrawTablePage(PrintPageEventArgs e, ExportTable table, ref int rowIndex)
        {
            using (var pen = new Pen(Color.Black))
            using (var normal = new Font("Microsoft YaHei", 12f))
            using (var bold = new Font("Microsoft YaHei", 12f, FontStyle.Bold))
            using (var title = new Font("Microsoft YaHei", 15f, FontStyle.Bold))
            {
                float[] x = TableWidths(e.Graphics, table, e.MarginBounds.Left, e.MarginBounds.Width, normal);
                float y = e.MarginBounds.Top;
                DrawCell(e.Graphics, pen, title, table.Title, x[0], y, x[x.Length - 1] - x[0], 38, StringAlignment.Center); y += 38;
                DrawCell(e.Graphics, pen, normal, "客户：" + table.Subject, x[0], y, x[x.Length - 1] - x[0], 32, StringAlignment.Near); y += 32;
                DrawCell(e.Graphics, pen, normal, "期间：" + table.Period, x[0], y, x[x.Length - 1] - x[0], 32, StringAlignment.Near); y += 32;
                string[] headers = table.Columns.Select(c => c.Header).ToArray();
                float headerHeight = MeasureRow(e.Graphics, bold, headers, x, 34);
                DrawRow(e.Graphics, pen, bold, headers, table.Columns, x, y, headerHeight, true); y += headerHeight;
                int totalRows = Math.Max(table.MinimumRows, table.Rows.Count), pageStart = rowIndex;
                int noteColumn = table.Columns.ToList().FindIndex(c => c.Header == "备注");
                while (rowIndex < totalRows)
                {
                    object[] values = rowIndex < table.Rows.Count ? table.Rows[rowIndex] : new object[table.Columns.Count];
                    string[] text = values.Select(ValueText).ToArray(); if (noteColumn >= 0) text[noteColumn] = ""; float height = MeasureRow(e.Graphics, normal, text, x, 34);
                    float reserve = rowIndex == totalRows - 1 ? table.Summaries.Count * 38 : 0;
                    if (y + height + reserve > e.MarginBounds.Bottom && rowIndex > pageStart) { e.HasMorePages = true; return; }
                    DrawRow(e.Graphics, pen, normal, text, table.Columns, x, y, height, false); y += height; rowIndex++;
                }
                foreach (var summary in table.Summaries)
                {
                    if (summary.RightValue.HasValue && table.Columns.Count >= 4)
                    {
                        int n = table.Columns.Count;
                        DrawCell(e.Graphics, pen, bold, summary.Label, x[0], y, x[1] - x[0], 38, StringAlignment.Center);
                        DrawCell(e.Graphics, pen, normal, summary.Text, x[1], y, x[n - 2] - x[1], 38, StringAlignment.Near);
                        DrawCell(e.Graphics, pen, bold, summary.RightLabel, x[n - 2], y, x[n - 1] - x[n - 2], 38, StringAlignment.Center);
                        DrawCell(e.Graphics, pen, bold, summary.RightValue.Value.ToString("0.00"), x[n - 1], y, x[n] - x[n - 1], 38, StringAlignment.Far);
                    }
                    else
                    {
                        int labelColumns = table.Columns.Count > 8 ? 2 : 1;
                        DrawCell(e.Graphics, pen, bold, summary.Label, x[0], y, x[labelColumns] - x[0], 38, StringAlignment.Center);
                        DrawCell(e.Graphics, pen, bold, summary.Text, x[labelColumns], y, x[x.Length - 1] - x[labelColumns], 38, StringAlignment.Near);
                    }
                    y += 38;
                }
                e.HasMorePages = false;
            }
        }

        static float[] TableWidths(Graphics g, ExportTable table, float left, float available, Font font)
        {
            int count = table.Columns.Count; float[] widths = table.Columns.Select(c => c.MinPoints).ToArray(); float minimum = widths.Sum();
            if (minimum > available) for (int i = 0; i < count; i++) widths[i] *= available / minimum;
            else
            {
                float remaining = available - minimum; var weights = new float[count];
                for (int i = 0; i < count; i++)
                {
                    int length = table.Columns[i].Header.Length * 2;
                    foreach (var row in table.Rows) length = Math.Max(length, Math.Min(30, DisplayLength(ValueText(row[i]))));
                    weights[i] = Math.Max(1, length);
                }
                float totalWeight = weights.Sum();
                for (int i = 0; i < count; i++) widths[i] = Math.Min(table.Columns[i].MaxPoints, widths[i] + remaining * weights[i] / totalWeight);
                remaining = available - widths.Sum();
                for (int i = 0; i < count; i++) widths[i] += remaining * weights[i] / totalWeight;
            }
            float[] x = new float[count + 1]; x[0] = left; for (int i = 0; i < count; i++) x[i + 1] = x[i] + widths[i]; return x;
        }

        static int DisplayLength(string value) { return (value ?? "").Sum(c => c > 255 ? 2 : 1); }
        static string ValueText(object value) { if (value == null) return ""; if (value is decimal) return ((decimal)value).ToString("0.00", CultureInfo.InvariantCulture); return Convert.ToString(value, CultureInfo.InvariantCulture); }

        static float MeasureRow(Graphics g, Font font, string[] values, float[] x, float minimum)
        {
            float height = minimum;
            using (var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord })
                for (int i = 0; i < values.Length; i++) height = Math.Max(height, g.MeasureString(values[i] ?? "", font, Math.Max(1, (int)(x[i + 1] - x[i] - 8)), format).Height + 8);
            return height;
        }

        static void DrawRow(Graphics g, Pen pen, Font font, string[] values, IList<ExportColumn> columns, float[] x, float y, float height, bool header)
        {
            for (int i = 0; i < values.Length; i++) DrawCell(g, pen, font, values[i] ?? "", x[i], y, x[i + 1] - x[i], height,
                header ? StringAlignment.Center : (columns[i].Money || columns[i].Integer ? StringAlignment.Far : StringAlignment.Near), !header && (columns[i].Money || columns[i].Integer));
        }

        static void DrawCell(Graphics g, Pen pen, Font font, string value, float x, float y, float width, float height, StringAlignment alignment, bool noWrap = false)
        {
            g.DrawRectangle(pen, x, y, width, height);
            float padding = noWrap ? 2 : 4;
            using (var format = new StringFormat { Alignment = alignment, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord, FormatFlags = noWrap ? StringFormatFlags.NoWrap : 0 })
                g.DrawString(value, font, Brushes.Black, new RectangleF(x + padding, y, Math.Max(1, width - padding * 2), height), format);
        }

        void SavePayment()
        {
            var c = paymentCustomer.SelectedItem as Customer; if (c == null) { MessageBox.Show("请从列表中选择已有客户。"); return; }
            long amount; try { amount = Money.Parse(paymentAmount.Text); } catch (Exception ex) { MessageBox.Show(ex.Message); return; }
            if (amount <= 0) { MessageBox.Show("还款金额必须大于零。"); return; }
            long balance = store.Balance(c.Id);
            if (amount > balance && MessageBox.Show("还款将超过当前欠款并形成客户结余，仍要保存吗？", "金额确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var entry = new LedgerEntry { CustomerId = c.Id, Kind = "payment", Date = paymentDate.Value.ToString("yyyy-MM-dd HH:mm"), AmountCents = amount,
                Details = paymentMethod.Text.Trim().Length == 0 ? "还款" : paymentMethod.Text.Trim(), Note = paymentNote.Text.Trim() };
            store.Data.Entries.Add(entry); store.Audit("新增", "还款", entry.Id, "记还款", null, entry); SaveAndRefresh();
            paymentAmount.Clear(); paymentNote.Clear(); MessageBox.Show("已记录，" + c.Name + " 当前" + Money.Label(store.Balance(c.Id)) + "。", "保存成功");
        }

        void EditSelectedEntry()
        {
            var row = Selected<StatementRow>(statementGrid); if (row == null || row.Entry == null) return;
            var e = row.Entry; var before = Clone(e);
            if (e.Kind == "sale" && e.Items != null && e.Items.Count > 0) { LoadSaleForEdit(e); return; }
            using (var d = new RecordDialog("修改" + (e.Kind == "sale" ? "消费" : "还款"), new[] { "日期", e.Kind == "sale" ? "商品明细" : "还款方式", "金额（元）", "备注" },
                new[] { e.Date, e.Details, Money.Text(e.AmountCents), e.Note }))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                DateTime date; if (!DateTime.TryParse(d.Values[0], out date)) { MessageBox.Show("日期格式不正确。"); return; }
                long amount; try { amount = Money.Parse(d.Values[2]); } catch (Exception ex) { MessageBox.Show(ex.Message); return; }
                if (amount <= 0) { MessageBox.Show("金额必须大于零。"); return; }
                e.Date = date.ToString("yyyy-MM-dd HH:mm"); e.Details = d.Values[1].Trim(); e.AmountCents = amount; e.Note = d.Values[3].Trim(); e.ModifiedUtc = DateTime.UtcNow.ToString("o");
                store.Audit("修改", e.Kind == "sale" ? "消费" : "还款", e.Id, "用户修改", before, e); SaveAndRefresh(); LoadStatement();
            }
        }

        void DeleteSelectedEntry()
        {
            var row = Selected<StatementRow>(statementGrid); if (row == null || row.Entry == null) return;
            if (MessageBox.Show("确定删除选中的账目吗？记录会保留在审计日志中。", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var before = Clone(row.Entry); row.Entry.Deleted = true; row.Entry.ModifiedUtc = DateTime.UtcNow.ToString("o");
            store.Audit("删除", row.Entry.Kind == "sale" ? "销售单" : "还款", row.Entry.Id, "用户删除", before, row.Entry); SaveAndRefresh(); LoadStatement();
        }

        void LoadStatement()
        {
            var choice = statementCustomer.SelectedItem as CustomerChoice;
            if (choice == null && statementCustomer.Text.Trim().Length > 0 && statementCustomer.Text.Trim() != "全部客户") { MessageBox.Show("请选择列表中的客户，或清空客户框查询全部客户。"); return; }
            var selectedCustomer = choice == null ? null : choice.Customer;
            DateTime from = fromDate.Value.Date, to = toDate.Value.Date;
            if (from > to) { MessageBox.Show("开始日期不能晚于结束日期。"); return; }
            var rows = new List<StatementRow>(); currentStatementSales = 0; currentStatementPayments = 0; int line = 0;
            var customers = selectedCustomer == null ? store.Data.Customers.ToList() : new List<Customer> { selectedCustomer };
            foreach (var c in customers)
            {
                long running = DateTime.Parse(c.OpeningDate).Date < from ? c.OpeningCents : 0;
                foreach (var e in store.Data.Entries.Where(x => !x.Deleted && x.CustomerId == c.Id && EntryTime(x) < from)) running += e.Kind == "sale" ? e.AmountCents : -e.AmountCents;
                if (selectedCustomer != null) currentStatementOpening = running;
                var periodEntries = store.Data.Entries.Where(x => !x.Deleted && x.CustomerId == c.Id && EntryTime(x) >= from && EntryTime(x) < to.AddDays(1)).ToList();
                DateTime openingDate = DateTime.Parse(c.OpeningDate).Date;
                if (c.OpeningCents != 0 && openingDate >= from && openingDate < to.AddDays(1))
                    periodEntries.Add(new LedgerEntry { Id = "", CustomerId = c.Id, Kind = "sale", Date = openingDate.ToString("yyyy-MM-dd HH:mm"), Details = "期初欠款", AmountCents = c.OpeningCents, CreatedUtc = "" });
                foreach (var e in periodEntries.OrderBy(x => EntryTime(x)).ThenBy(x => x.CreatedUtc))
                {
                    running += e.Kind == "sale" ? e.AmountCents : -e.AmountCents;
                    if (e.Id.Length > 0 && e.Kind == "sale") currentStatementSales += e.AmountCents;
                    if (e.Kind == "payment") currentStatementPayments += e.AmountCents;
                    var items = e.Kind == "sale" && e.Items != null && e.Items.Count > 0 ? e.Items : null;
                    if (items != null)
                    {
                        for (int i = 0; i < items.Count; i++)
                        {
                            var item = items[i]; string note = item.Note ?? "";
                            if (i == items.Count - 1) note = JoinNote(note, "余额 ¥" + Money.Text(running));
                            rows.Add(StatementItem(++line, c, e, item.FullName, item.ShortName, item.Quantity > 0 ? item.Quantity : item.PieceCount,
                                item.Unit, item.PriceCents, item.AmountCents, note));
                        }
                    }
                    else
                    {
                        string fullName = e.Id.Length == 0 ? "期初欠款" : (e.Kind == "payment" ? "还款" : "销售");
                        string shortName = e.Id.Length == 0 ? "" : e.Details;
                        rows.Add(StatementItem(++line, c, e, fullName, shortName, 0, "", 0, e.Kind == "payment" ? -e.AmountCents : e.AmountCents,
                            JoinNote(e.Note, "余额 ¥" + Money.Text(running))));
                    }
                }
                if (selectedCustomer != null) currentStatementClosing = running;
            }
            if (statementSort.Text == "客户")
                rows = sortDescending ? rows.OrderByDescending(x => x.Customer).ThenByDescending(x => x.SortTime).ThenBy(x => x.Line).ToList() : rows.OrderBy(x => x.Customer).ThenByDescending(x => x.SortTime).ThenBy(x => x.Line).ToList();
            else
                rows = sortDescending ? rows.OrderByDescending(x => x.SortTime).ThenByDescending(x => x.CreatedUtc).ThenBy(x => x.Line).ToList() : rows.OrderBy(x => x.SortTime).ThenBy(x => x.CreatedUtc).ThenBy(x => x.Line).ToList();
            for (int i = 0; i < rows.Count; i++) rows[i].Sequence = i + 1;
            currentStatement = rows; currentStatementCustomer = selectedCustomer; statementLoaded = true;
            statementGrid.DataSource = rows;
            if (statementGrid.Columns["Entry"] != null) statementGrid.Columns["Entry"].Visible = false;
            if (statementGrid.Columns["SortTime"] != null) statementGrid.Columns["SortTime"].Visible = false;
            if (statementGrid.Columns["CreatedUtc"] != null) statementGrid.Columns["CreatedUtc"].Visible = false;
            if (statementGrid.Columns["Line"] != null) statementGrid.Columns["Line"].Visible = false;
            SetHeaders(statementGrid, new Dictionary<string, string> { { "Sequence", "序号" }, { "Customer", "客户" }, { "Date", "时间" }, { "FullName", "商品全名" }, { "ShortName", "商品名称" }, { "Quantity", "数量" }, { "Unit", "单位" }, { "Price", "单价" }, { "Amount", "金额" }, { "Note", "备注" } });
            if (selectedCustomer == null)
            {
                long totalDebt = store.Data.Customers.Sum(x => Math.Max(0, store.Balance(x.Id)));
                statementBalance.Text = "期间销售 ¥" + Money.Text(currentStatementSales) + "    期间还款 ¥" + Money.Text(currentStatementPayments) + "    当前总欠款 ¥" + Money.Text(totalDebt);
            }
            else statementBalance.Text = "期初 ¥" + Money.Text(currentStatementOpening) + "    期间销售 ¥" + Money.Text(currentStatementSales) + "    期间还款 ¥" + Money.Text(currentStatementPayments) + "    期末 " + Money.Label(currentStatementClosing);
        }

        static StatementRow StatementItem(int line, Customer customer, LedgerEntry entry, string fullName, string shortName, int quantity, string unit, long price, long amount, string note)
        {
            return new StatementRow { Line = line, Customer = customer.Name, Date = EntryTime(entry).ToString("yyyy-MM-dd HH:mm"), FullName = fullName,
                ShortName = shortName, Quantity = quantity == 0 ? "" : quantity.ToString(), Unit = unit ?? "", Price = price == 0 ? "" : SaleExportFormatter.Amount(price),
                Amount = SaleExportFormatter.Amount(amount), Note = note ?? "", Entry = entry.Id.Length == 0 ? null : entry, SortTime = EntryTime(entry), CreatedUtc = entry.CreatedUtc };
        }

        static string JoinNote(string first, string second) { return string.IsNullOrWhiteSpace(first) ? second : first.Trim() + "；" + second; }

        ExportTable CurrentStatementTable()
        {
            if (!statementLoaded) { MessageBox.Show("请先查询账单。"); return null; }
            string subject = currentStatementCustomer == null ? "全部客户" : currentStatementCustomer.Name;
            var table = new ExportTable { Title = "账单清单", Subject = subject, Period = fromDate.Value.ToString("yyyy/M/d") + " 至 " + toDate.Value.ToString("yyyy/M/d"), FileName = SaleExportFormatter.SafeFileName(subject + "_账单_" + DateTime.Now.ToString("yyyyMMdd")) };
            table.Columns.AddRange(new[] { new ExportColumn("序号", 44, 50, 6, 7, false, true), new ExportColumn("客户", 62, 96, 10, 16),
                new ExportColumn("时间", 82, 105, 15, 18), new ExportColumn("商品全名", 82, 132, 13, 23), new ExportColumn("商品名称", 68, 108, 11, 19),
                new ExportColumn("数量", 40, 48, 7, 8, false, true), new ExportColumn("单位", 38, 46, 6, 7), new ExportColumn("单价", 58, 72, 9, 11, true),
                new ExportColumn("金额", 58, 72, 10, 12, true), new ExportColumn("备注", 82, 125, 13, 22) });
            foreach (var r in currentStatement) table.Rows.Add(new object[] { r.Sequence, r.Customer, r.Date, r.FullName, r.ShortName,
                r.Quantity.Length == 0 ? null : (object)int.Parse(r.Quantity), r.Unit, r.Price.Length == 0 ? null : (object)decimal.Parse(r.Price, CultureInfo.InvariantCulture),
                decimal.Parse(r.Amount, CultureInfo.InvariantCulture), "" });
            table.Summaries.Add(new ExportSummary { Label = "汇总", Text = statementBalance.Text });
            return table;
        }

        void ExportStatementXlsx()
        {
            var table = CurrentStatementTable(); if (table == null) return;
            using (var save = new SaveFileDialog { Filter = "Excel 工作簿|*.xlsx", FileName = table.FileName + ".xlsx" })
                if (save.ShowDialog(this) == DialogResult.OK) try { XlsxWriter.Write(save.FileName, table); MessageBox.Show("账单表格已导出。\r\n" + save.FileName, "导出成功"); }
                catch (Exception ex) { MessageBox.Show("表格导出失败：\r\n" + ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void PrintStatement()
        {
            var table = CurrentStatementTable(); if (table == null) return;
            using (var doc = new PrintDocument())
            {
                int rowIndex = 0; doc.DocumentName = table.FileName; doc.DefaultPageSettings.Landscape = true; doc.DefaultPageSettings.PaperSize = new PaperSize("A4", 827, 1169); doc.DefaultPageSettings.Margins = new Margins(24, 24, 28, 28);
                doc.PrintPage += delegate(object sender, PrintPageEventArgs e) { DrawTablePage(e, table, ref rowIndex); };
                using (var dialog = new PrintDialog { Document = doc, UseEXDialog = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) try { doc.Print(); } catch (Exception ex) { MessageBox.Show("打印失败：" + ex.Message); }
            }
        }

        void Backup()
        {
            using (var save = new SaveFileDialog { Filter = "随身赊账本备份|*.szbbackup", InitialDirectory = store.DataDirectory, FileName = "随身赊账本_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".szbbackup" })
                if (save.ShowDialog(this) == DialogResult.OK) { store.Backup(save.FileName); MessageBox.Show("加密备份已保存到：\r\n" + save.FileName); }
        }

        void Restore()
        {
            if (saleDirty) { MessageBox.Show("恢复前请先保存当前销售单，或点击“新销售单”确认放弃草稿。", "存在未保存草稿", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string latest = store.LatestBackup();
            using (var open = new OpenFileDialog { Filter = "随身赊账本备份|*.szbbackup", InitialDirectory = store.DataDirectory, FileName = latest == null ? "" : Path.GetFileName(latest) })
            {
                if (open.ShowDialog(this) != DialogResult.OK) return;
                using (var p = new PasswordForm(false))
                {
                    p.Text = "输入备份文件的密码"; if (p.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        if (MessageBox.Show("恢复会用备份内容替换当前账本，程序会先自动保护当前账本。继续吗？", "确认恢复", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                        store.Restore(open.FileName, p.PasswordValue); RefreshAll(); MessageBox.Show("恢复完成。");
                    }
                    catch (Exception ex) { MessageBox.Show("恢复失败，当前账本未被替换：\r\n" + ex.Message); }
                }
            }
        }

        void CheckUpdate()
        {
            if (saleDirty) { MessageBox.Show("更新前请先保存当前销售单，或点击“新销售单”确认放弃草稿。"); return; }
            string repo = store.Data.GitHubRepository.Trim();
            if (repo.Split('/').Length != 2) { MessageBox.Show("请先在“设置”中填写 GitHub 仓库，格式为 owner/repository。"); return; }
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (var web = new TimeoutWebClient())
                {
                    web.Headers[HttpRequestHeader.UserAgent] = "SuishenLedger/" + Assembly.GetExecutingAssembly().GetName().Version;
                    string json = web.DownloadString("https://api.github.com/repos/" + repo + "/releases/latest");
                    var release = new JavaScriptSerializer().Deserialize<GitHubRelease>(json);
                    Version latest; if (release == null || !Version.TryParse((release.tag_name ?? "").TrimStart('v', 'V'), out latest)) throw new InvalidDataException("更新信息格式不正确。");
                    Version current = Assembly.GetExecutingAssembly().GetName().Version;
                    if (latest <= current) { MessageBox.Show("当前已是最新版本 " + current.ToString(3) + "。"); return; }
                    if (MessageBox.Show("发现版本 " + latest + "\r\n\r\n" + release.body + "\r\n\r\n是否下载？账本数据不会被替换。", "发现更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
                    var exe = release.assets == null ? null : release.assets.FirstOrDefault(x => x.name == "suishen-ledger.exe");
                    var sum = release.assets == null ? null : release.assets.FirstOrDefault(x => x.name == "suishen-ledger.exe.sha256");
                    if (exe == null || sum == null) throw new InvalidDataException("该版本缺少更新文件或校验文件。");
                    string updates = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Updates"); Directory.CreateDirectory(updates);
                    string downloaded = Path.Combine(updates, "suishen-ledger-" + latest + ".exe");
                    using (var progress = new DownloadProgressForm(web, new Uri(exe.browser_download_url), downloaded))
                    {
                        if (progress.ShowDialog(this) != DialogResult.OK) { if (progress.Error != null) throw progress.Error; return; }
                    }
                    string expected = web.DownloadString(sum.browser_download_url).Trim().Split(' ')[0].ToLowerInvariant();
                    string actual; using (var sha = SHA256.Create()) using (var fs = File.OpenRead(downloaded)) actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                    if (expected != actual) { File.Delete(downloaded); throw new CryptographicException("更新文件校验失败，已拒绝安装。"); }
                    var test = Process.Start(new ProcessStartInfo(downloaded, "--self-test") { UseShellExecute = false, CreateNoWindow = true }); test.WaitForExit();
                    if (test.ExitCode != 0) { File.Delete(downloaded); throw new InvalidDataException("新版自检失败，已取消安装。"); }
                    PrepareUpdate(downloaded);
                }
            }
            catch (Exception ex) { MessageBox.Show("检查更新失败，不影响继续记账：\r\n" + ex.Message, "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void PrepareUpdate(string downloaded)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string current = Application.ExecutablePath;
            string dataBackup = Path.Combine(baseDir, "Data", "升级前备份_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".szbbackup");
            store.Backup(dataBackup);
            string cmd = Path.Combine(baseDir, "Updates", "apply-update.cmd");
            string currentName = Path.GetFileName(current);
            string downloadedName = Path.GetFileName(downloaded);
            File.WriteAllText(cmd,
                "@echo off\r\n" +
                "setlocal\r\n" +
                "cd /d \"%~dp0\"\r\n" +
                "echo [%date% %time%] 开始安装 >> update.log\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                "copy /y \"..\\" + currentName + "\" \"..\\" + currentName + ".old\" >nul\r\n" +
                "copy /y \"" + downloadedName + "\" \"..\\" + currentName + "\" >nul\r\n" +
                "if errorlevel 1 goto rollback\r\n" +
                "\"..\\" + currentName + "\" --self-test\r\n" +
                "if errorlevel 1 goto rollback\r\n" +
                "del /q update-ready.flag 2>nul\r\n" +
                "start \"\" \"..\\" + currentName + "\" --update-ready \"%cd%\\update-ready.flag\"\r\n" +
                "for /l %%i in (1,1,15) do (if exist update-ready.flag goto success & timeout /t 1 /nobreak >nul)\r\n" +
                ":rollback\r\n" +
                "echo [%date% %time%] 安装失败，恢复旧版 >> update.log\r\n" +
                "copy /y \"..\\" + currentName + ".old\" \"..\\" + currentName + "\" >nul\r\n" +
                "start \"\" \"..\\" + currentName + "\"\r\n" +
                "exit /b 1\r\n" +
                ":success\r\n" +
                "echo [%date% %time%] 安装成功 >> update.log\r\n" +
                "del /q update-ready.flag 2>nul\r\n" +
                "exit /b 0\r\n", Encoding.Default);
            if (MessageBox.Show("更新已验证，升级前账本备份也已完成。现在关闭软件并安装吗？", "准备安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"\"" + cmd + "\"\"") { CreateNoWindow = true, UseShellExecute = false });
                Application.Exit();
            }
        }

        void ChangePassword()
        {
            if (saleDirty) { MessageBox.Show("修改密码前请先保存当前销售单，或点击“新销售单”确认放弃草稿。"); return; }
            using (var p = new PasswordForm(true))
            {
                p.Text = "设置新密码";
                if (p.ShowDialog(this) == DialogResult.OK) { try { store.ChangePassword(p.PasswordValue); MessageBox.Show("密码已修改，请使用新密码打开账本和之后创建的备份。"); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
            }
        }

        void RemindBackup(FormClosingEventArgs e)
        {
            DateTime last;
            if (!DateTime.TryParse(store.Data.LastBackupUtc, out last) || DateTime.UtcNow - last.ToUniversalTime() > TimeSpan.FromDays(7))
                if (MessageBox.Show("账本超过 7 天没有备份。关闭前现在备份吗？", "备份提醒", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) Backup();
        }

        void ConfirmClosing(object sender, FormClosingEventArgs e)
        {
            if (saleDirty)
            {
                if (MessageBox.Show("销售清单中还有未上传的内容，确定关闭软件吗？下次打开可以恢复草稿。", "未上传的销售清单", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) { e.Cancel = true; return; }
                saleDraftTimer.Stop(); if (!SaveSaleDraft(true)) { e.Cancel = true; return; }
            }
            RemindBackup(e);
        }

        bool SaveAndRefresh()
        {
            try { store.Save(); RefreshAll(); return true; }
            catch (Exception ex)
            {
                try { store.Reload(); RefreshAll(); } catch { }
                MessageBox.Show("保存失败，已恢复磁盘中最后有效的账本。请不要拔出U盘：\r\n" + ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        void RefreshAll()
        {
            var selectedSale = saleCustomer.SelectedItem as Customer; var selectedPayment = paymentCustomer.SelectedItem as Customer;
            var selectedChoice = statementCustomer.SelectedItem as CustomerChoice; var selectedStatement = selectedChoice == null ? null : selectedChoice.Customer;
            var customers = store.Data.Customers.OrderByDescending(x => x.Active).ThenBy(x => x.Name).ToList();
            RefreshCustomers();
            var saleCustomers = customers.Where(x => x.Active || (editingSale != null && x.Id == editingSale.CustomerId)).ToList();
            loadingSale = true;
            try { BindCustomer(saleCustomer, saleCustomers, selectedSale); }
            finally { loadingSale = false; }
            BindCustomer(paymentCustomer, customers.Where(x => x.Active).ToList(), selectedPayment); BindStatementCustomers(customers, selectedStatement);
            RefreshProducts();
            RefreshSaleChoices();
            var balances = customers.ToDictionary(x => x.Id, x => store.Balance(x.Id));
            long total = balances.Values.Sum(x => Math.Max(0, x)); int debtors = balances.Values.Count(x => x > 0);
            summary.Text = (store.Data.ShopName.Length == 0 ? "随身赊账本" : store.Data.ShopName) + "    总欠款 ¥" + Money.Text(total) + "    欠款客户 " + debtors + " 人";
            if (statementLoaded) LoadStatement();
        }

        void UpdateSaleTotal()
        {
            long total = 0;
            foreach (DataGridViewRow row in saleGrid.Rows) { long amount; try { amount = Money.Parse(Cell(row, "Amount")); } catch { amount = 0; } total += amount; }
            saleTotal.Text = "¥" + Money.Text(total);
        }

        static void BindCustomer(ComboBox box, List<Customer> data, Customer selected)
        {
            box.DataSource = null; box.DataSource = data;
            if (selected != null) box.SelectedItem = data.FirstOrDefault(x => x.Id == selected.Id);
        }

        void BindStatementCustomers(List<Customer> customers, Customer selected)
        {
            var choices = new List<CustomerChoice> { new CustomerChoice { Text = "全部客户" } };
            choices.AddRange(customers.Select(x => new CustomerChoice { Text = x.ToString(), Customer = x }));
            statementCustomer.DataSource = null; statementCustomer.DataSource = choices;
            statementCustomer.SelectedItem = selected == null ? choices[0] : choices.FirstOrDefault(x => x.Customer != null && x.Customer.Id == selected.Id);
        }

        void RefreshCustomers()
        {
            string query = customerSearch.Text.Trim();
            var customers = store.Data.Customers.OrderByDescending(x => x.Active).ThenBy(x => x.Name)
                .Where(x => query.Length == 0 || x.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 || x.Phone.Contains(query)).ToList();
            customerGrid.DataSource = customers.Select(x => new CustomerRow { Name = x.Name, Phone = x.Phone, Balance = Money.Label(store.Balance(x.Id)), Status = x.Active ? "使用中" : "已停用", Customer = x }).ToList();
            if (customerGrid.Columns["Customer"] != null) customerGrid.Columns["Customer"].Visible = false;
            SetHeaders(customerGrid, new Dictionary<string, string> { { "Name", "姓名" }, { "Phone", "手机号" }, { "Balance", "当前余额" }, { "Status", "状态" } });
        }

        void RefreshProducts()
        {
            string query = productSearch.Text.Trim(); IEnumerable<Product> products = store.Data.Products;
            products = products.Where(x => query.Length == 0 || x.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 || x.ShortName.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
            products = productSortAscending ? products.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase) : products.OrderByDescending(x => x.Name, StringComparer.CurrentCultureIgnoreCase);
            productGrid.DataSource = products.Select(x => new ProductRow { FullName = x.Name, ShortName = x.ShortName, Unit = x.Unit, Price = Money.Text(x.PriceCents), Status = x.Active ? (x.PriceCents > 0 ? "使用中" : "请补单价") : "已停用", Product = x }).ToList();
            if (productGrid.Columns["Product"] != null) productGrid.Columns["Product"].Visible = false;
            SetHeaders(productGrid, new Dictionary<string, string> { { "FullName", "商品全名" }, { "ShortName", "商品名称" }, { "Unit", "单位" }, { "Price", "默认单价" }, { "Status", "状态" } });
        }

        static void SetHeaders(DataGridView grid, Dictionary<string, string> headers)
        {
            foreach (var pair in headers) if (grid.Columns[pair.Key] != null) grid.Columns[pair.Key].HeaderText = pair.Value;
        }

        static DateTime EntryTime(LedgerEntry entry)
        {
            DateTime value; return DateTime.TryParse(entry.Date, out value) ? value : DateTime.MinValue;
        }

        void UpdateSortButton()
        {
            statementDirection.Text = statementSort.Text == "客户" ? (sortDescending ? "降序" : "升序") : (sortDescending ? "新 → 旧" : "旧 → 新");
        }

        static T Selected<T>(DataGridView grid) where T : class
        {
            if (grid.CurrentRow == null) return null;
            object source = grid.CurrentRow.DataBoundItem;
            if (source is T) return source as T;
            var property = source == null ? null : source.GetType().GetProperties().FirstOrDefault(x => x.PropertyType == typeof(T));
            return property == null ? null : property.GetValue(source, null) as T;
        }

        static T Clone<T>(T value) { var json = new JavaScriptSerializer(); return json.Deserialize<T>(json.Serialize(value)); }
        static DataGridView Grid() { var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, RowHeadersVisible = false }; grid.RowTemplate.Height = 36; grid.ColumnHeadersHeight = 40; return grid; }
        static ComboBox SearchCombo() { return new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteSource = AutoCompleteSource.ListItems, AutoCompleteMode = AutoCompleteMode.SuggestAppend }; }
        static DateTimePicker DatePicker() { return new DateTimePicker { Width = 155, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd" }; }
        static DateTimePicker MinutePicker() { return new DateTimePicker { Width = 210, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm" }; }
        static TextBox Box() { return new TextBox { Width = 280 }; }
        static Button Button(string text, EventHandler click) { var b = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(4) }; b.Click += click; return b; }
        static FlowLayoutPanel Bar() { return new FlowLayoutPanel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(8), BackColor = Color.FromArgb(248, 249, 251), WrapContents = false, AutoScroll = true }; }
        static TableLayoutPanel FormTable() { var p = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(50, 35, 50, 35), ColumnCount = 2, AutoScroll = true }; p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return p; }
        static void AddRow(TableLayoutPanel p, int row, string label, Control control) { p.RowStyles.Add(new RowStyle(SizeType.AutoSize)); var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 9, 0, 0) }; control.Margin = new Padding(4, 6, 4, 10); p.Controls.Add(l, 0, row); p.Controls.Add(control, 1, row); }
    }

    public sealed class RecordDialog : Form
    {
        readonly List<TextBox> boxes = new List<TextBox>();
        public string[] Values { get { return boxes.Select(x => x.Text).ToArray(); } }
        public RecordDialog(string title, string[] labels, string[] values)
        {
            Text = title; Font = new Font("Microsoft YaHei UI", 12F); AutoScaleMode = AutoScaleMode.Dpi;
            Width = 620; Height = Math.Min(760, 155 + labels.Length * 64); StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, AutoScroll = true };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < labels.Length; i++)
            {
                var box = new TextBox { Text = values[i], Width = 270 }; boxes.Add(box);
                table.Controls.Add(new Label { Text = labels[i], AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, i); table.Controls.Add(box, 1, i);
            }
            var ok = new Button { Text = "确定", AutoSize = true, DialogResult = DialogResult.OK }; var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
            var flow = new FlowLayoutPanel { AutoSize = true }; flow.Controls.Add(ok); flow.Controls.Add(cancel); table.Controls.Add(flow, 1, labels.Length);
            Controls.Add(table); AcceptButton = ok; CancelButton = cancel;
        }
    }

    public sealed class ProductDialog : Form
    {
        readonly TextBox fullName = new TextBox { Width = 330 };
        readonly TextBox shortName = new TextBox { Width = 330 };
        readonly ComboBox unit = new ComboBox { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly TextBox price = new TextBox { Width = 200 };
        public string FullName { get { return fullName.Text; } }
        public string ShortName { get { return shortName.Text; } }
        public string Unit { get { return Convert.ToString(unit.SelectedItem); } }
        public string PriceText { get { return price.Text; } }

        public ProductDialog(Product product)
        {
            Text = product == null ? "新增商品" : "修改商品"; Font = new Font("Microsoft YaHei UI", 12F); AutoScaleMode = AutoScaleMode.Dpi;
            Width = 620; Height = 430; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            unit.Items.Add("个"); unit.Items.Add("件");
            if (product != null && !unit.Items.Contains(product.Unit)) unit.Items.Add(product.Unit);
            if (product == null) { unit.SelectedItem = "个"; price.Text = ""; }
            else { fullName.Text = product.Name; shortName.Text = product.ShortName; unit.SelectedItem = product.Unit; price.Text = Money.Text(product.PriceCents); }
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = 5 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Add(table, 0, "商品全名", fullName); Add(table, 1, "商品名称", shortName); Add(table, 2, "单位", unit); Add(table, 3, "默认单价（元）", price);
            var ok = new Button { Text = "确定", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
            var cancel = new Button { Text = "取消", AutoSize = true, Padding = new Padding(10, 4, 10, 4), DialogResult = DialogResult.Cancel };
            var buttons = new FlowLayoutPanel { AutoSize = true }; buttons.Controls.Add(ok); buttons.Controls.Add(cancel); table.Controls.Add(buttons, 1, 4);
            ok.Click += delegate
            {
                long cents;
                if (fullName.Text.Trim().Length == 0) { MessageBox.Show("商品全名不能为空。"); return; }
                try { cents = Money.Parse(price.Text); } catch (Exception ex) { MessageBox.Show(ex.Message); return; }
                if (cents <= 0) { MessageBox.Show("默认单价必须大于零。"); return; }
                DialogResult = DialogResult.OK; Close();
            };
            Controls.Add(table); AcceptButton = ok; CancelButton = cancel;
        }

        static void Add(TableLayoutPanel table, int row, string label, Control control)
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 10, 0, 0) }, 0, row); table.Controls.Add(control, 1, row);
        }
    }

    public sealed class ExportColumn
    {
        public string Header; public float MinPoints; public float MaxPoints; public double MinChars; public double MaxChars; public bool Money; public bool Integer;
        public ExportColumn(string header, float minPoints, float maxPoints, double minChars, double maxChars, bool money = false, bool integer = false)
        { Header = header; MinPoints = minPoints; MaxPoints = maxPoints; MinChars = minChars; MaxChars = maxChars; Money = money; Integer = integer; }
    }

    public sealed class ExportTable
    {
        public string Title = ""; public string Subject = ""; public string Period = ""; public string FileName = "";
        public List<ExportColumn> Columns = new List<ExportColumn>(); public List<object[]> Rows = new List<object[]>(); public List<ExportSummary> Summaries = new List<ExportSummary>();
        public int MinimumRows;
    }

    public sealed class ExportSummary
    {
        public string Label = ""; public string Text = ""; public string RightLabel = ""; public decimal? RightValue;
    }

    public static class XlsxWriter
    {
        public static void Write(string path, ExportTable table)
        {
            string target = Path.GetFullPath(path), temp = target + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
            try
            {
                using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    Entry(archive, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
                    Entry(archive, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                    Entry(archive, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"" + Xml(table.Title) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                    Entry(archive, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                    Entry(archive, "xl/styles.xml", Styles()); Entry(archive, "xl/worksheets/sheet1.xml", Sheet(table));
                }
                File.Copy(temp, target, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        static string Sheet(ExportTable table)
        {
            int count = table.Columns.Count, row = 1; string last = Column(count);
            var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"4\" topLeftCell=\"A5\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
            for (int i = 0; i < count; i++)
            {
                double width = table.Columns[i].MinChars;
                foreach (var values in table.Rows) width = Math.Max(width, DisplayLength(Convert.ToString(values[i], CultureInfo.InvariantCulture)) + 2);
                width = Math.Min(table.Columns[i].MaxChars, width);
                xml.Append("<col min=\"").Append(i + 1).Append("\" max=\"").Append(i + 1).Append("\" width=\"").Append(width.ToString("0.0", CultureInfo.InvariantCulture)).Append("\" customWidth=\"1\"/>");
            }
            xml.Append("</cols><sheetData>");
            xml.Append(Row(row, new[] { TextCell("A" + row, table.Title, 1) }, 30)); row++;
            xml.Append(Row(row, new[] { TextCell("A" + row, "", 2), TextCell("B" + row, "客户：" + table.Subject, 2) }, 24)); row++;
            xml.Append(Row(row, new[] { TextCell("A" + row, "", 2), TextCell("B" + row, "期间：" + table.Period, 2) }, 24)); row++;
            var header = new List<string>(); for (int i = 0; i < count; i++) header.Add(TextCell(Column(i + 1) + row, table.Columns[i].Header, 3));
            xml.Append(Row(row, header, 30)); row++;
            int detailCount = Math.Max(table.MinimumRows, table.Rows.Count);
            for (int r = 0; r < detailCount; r++)
            {
                var cells = new List<string>(); object[] values = r < table.Rows.Count ? table.Rows[r] : new object[count];
                for (int i = 0; i < count; i++)
                {
                    string reference = Column(i + 1) + row; object value = values[i]; ExportColumn column = table.Columns[i];
                    if (value != null && (column.Money || column.Integer)) cells.Add(NumberCell(reference, Convert.ToDecimal(value, CultureInfo.InvariantCulture), column.Money ? 4 : 5));
                    else cells.Add(TextCell(reference, Convert.ToString(value, CultureInfo.InvariantCulture), 2));
                }
                xml.Append(Row(row, cells, 28)); row++;
            }
            var merges = new List<string> { "A1:" + last + "1", "B2:" + last + "2", "B3:" + last + "3" };
            foreach (var summary in table.Summaries)
            {
                var cells = new List<string>();
                cells.Add(TextCell("A" + row, summary.Label, 6));
                if (summary.RightValue.HasValue && count >= 4)
                {
                    cells.Add(TextCell("B" + row, summary.Text, 2));
                    cells.Add(TextCell(Column(count - 1) + row, summary.RightLabel, 6));
                    cells.Add(NumberCell(last + row, summary.RightValue.Value, 6));
                    merges.Add("B" + row + ":" + Column(count - 2) + row);
                }
                else
                {
                    cells.Add(TextCell("B" + row, summary.Text, 6)); merges.Add("B" + row + ":" + last + row);
                }
                xml.Append(Row(row, cells, 30)); row++;
            }
            xml.Append("</sheetData><mergeCells count=\"").Append(merges.Count).Append("\">"); foreach (string merge in merges) xml.Append("<mergeCell ref=\"").Append(merge).Append("\"/>");
            xml.Append("</mergeCells><pageMargins left=\"0.3\" right=\"0.3\" top=\"0.4\" bottom=\"0.4\" header=\"0.2\" footer=\"0.2\"/><pageSetup paperSize=\"9\" orientation=\"portrait\" fitToWidth=\"1\" fitToHeight=\"0\"/></worksheet>");
            return xml.ToString();
        }

        static string Styles()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"0.00\"/></numFmts><fonts count=\"3\"><font><sz val=\"12\"/><name val=\"Microsoft YaHei\"/></font><font><b/><sz val=\"15\"/><name val=\"Microsoft YaHei\"/></font><font><b/><sz val=\"12\"/><name val=\"Microsoft YaHei\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"2\"><border/><border><left style=\"thin\"><color auto=\"1\"/></left><right style=\"thin\"><color auto=\"1\"/></right><top style=\"thin\"><color auto=\"1\"/></top><bottom style=\"thin\"><color auto=\"1\"/></bottom><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"7\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf><xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf><xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\"/></xf><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"right\" vertical=\"center\"/></xf><xf numFmtId=\"164\" fontId=\"2\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";
        }

        static string Row(int index, IEnumerable<string> cells, double height) { return "<row r=\"" + index + "\" ht=\"" + height.ToString(CultureInfo.InvariantCulture) + "\" customHeight=\"1\">" + string.Join("", cells.ToArray()) + "</row>"; }
        static string TextCell(string reference, string value, int style) { return "<c r=\"" + reference + "\" s=\"" + style + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">" + Xml(value ?? "") + "</t></is></c>"; }
        static string NumberCell(string reference, decimal value, int style) { return "<c r=\"" + reference + "\" s=\"" + style + "\"><v>" + value.ToString(CultureInfo.InvariantCulture) + "</v></c>"; }
        static string Column(int number) { string value = ""; while (number > 0) { number--; value = (char)('A' + number % 26) + value; number /= 26; } return value; }
        static int DisplayLength(string value) { return (value ?? "").Sum(c => c > 255 ? 2 : 1); }
        static string Xml(string value) { return (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;"); }
        static void Entry(ZipArchive archive, string name, string content) { var entry = archive.CreateEntry(name, CompressionLevel.Optimal); using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(content); }
    }

    public sealed class SaleExportDocument
    {
        public string CustomerName;
        public string Period;
        public List<SaleExportRow> Rows = new List<SaleExportRow>();
        public long TotalCents;
        public string Total { get { return SaleExportFormatter.Amount(TotalCents); } }
        public string UpperTotal { get { return SaleExportFormatter.RmbUpper(TotalCents); } }
    }

    public sealed class SaleExportRow
    {
        public int Sequence;
        public string Time = "";
        public string FullName = "";
        public string ShortName = "";
        public int Quantity;
        public string Unit = "";
        public string Price = "";
        public string Amount = "";
    }

    public static class SaleExportFormatter
    {
        static readonly string[] Digits = { "零", "壹", "贰", "叁", "肆", "伍", "陆", "柒", "捌", "玖" };
        static readonly string[] SmallUnits = { "", "拾", "佰", "仟" };

        public static SaleExportDocument Create(Customer customer, LedgerEntry entry)
        {
            DateTime when;
            if (!DateTime.TryParse(entry.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out when) && !DateTime.TryParse(entry.Date, out when)) when = DateTime.MinValue;
            string itemTime = when == DateTime.MinValue ? entry.Date : when.ToString("yyyy/M/d H:mm", CultureInfo.InvariantCulture);
            string period = when == DateTime.MinValue ? entry.Date.Split(' ')[0] : when.ToString("yyyy/M/d", CultureInfo.InvariantCulture);
            var result = new SaleExportDocument { CustomerName = customer.Name, Period = period, TotalCents = entry.AmountCents };
            int sequence = 0;
            foreach (var item in entry.Items ?? new List<SaleItem>())
                result.Rows.Add(new SaleExportRow { Sequence = ++sequence, Time = itemTime, FullName = item.FullName, ShortName = item.ShortName,
                    Quantity = item.Quantity > 0 ? item.Quantity : item.PieceCount, Unit = item.Unit, Price = Amount(item.PriceCents), Amount = Amount(item.AmountCents) });
            return result;
        }

        public static ExportTable Table(SaleExportDocument sale)
        {
            var table = new ExportTable { Title = "销售清单", Subject = sale.CustomerName, Period = sale.Period, FileName = SafeFileName(sale.CustomerName + "_销售清单"), MinimumRows = 5 };
            table.Columns.AddRange(new[] {
                new ExportColumn("序号", 34, 40, 6, 7, false, true), new ExportColumn("时间", 72, 88, 15, 18),
                new ExportColumn("商品全名", 86, 145, 14, 26), new ExportColumn("商品名称", 66, 110, 11, 20),
                new ExportColumn("数量", 40, 48, 7, 8, false, true), new ExportColumn("单位", 34, 42, 6, 7),
                new ExportColumn("单价", 54, 66, 9, 12, true), new ExportColumn("金额", 58, 72, 10, 13, true) });
            foreach (var row in sale.Rows) table.Rows.Add(new object[] { row.Sequence, row.Time, row.FullName, row.ShortName, row.Quantity, row.Unit,
                decimal.Parse(row.Price, CultureInfo.InvariantCulture), decimal.Parse(row.Amount, CultureInfo.InvariantCulture) });
            table.Summaries.Add(new ExportSummary { Label = "大写", Text = sale.UpperTotal, RightLabel = "总金额", RightValue = sale.TotalCents / 100m });
            return table;
        }

        public static string Amount(long cents) { return (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture); }

        public static string SafeFileName(string value)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            value = value.Trim().TrimEnd('.');
            return value.Length == 0 ? "销售清单" : value;
        }

        public static string RmbUpper(long cents)
        {
            if (cents < 0) throw new ArgumentOutOfRangeException("cents");
            long yuan = cents / 100; int jiao = (int)(cents / 10 % 10), fen = (int)(cents % 10);
            int[] groups = { (int)(yuan / 100000000), (int)(yuan / 10000 % 10000), (int)(yuan % 10000) };
            string[] units = { "亿", "万", "" }; var text = new StringBuilder(); bool zeroGroup = false;
            for (int i = 0; i < groups.Length; i++)
            {
                int group = groups[i];
                if (group == 0) { if (text.Length > 0) zeroGroup = true; continue; }
                if (text.Length > 0 && (zeroGroup || group < 1000)) text.Append("零");
                text.Append(UpperGroup(group)).Append(units[i]); zeroGroup = false;
            }
            if (text.Length == 0) text.Append("零");
            text.Append("元");
            if (jiao == 0 && fen == 0) return text.Append("整").ToString();
            if (jiao > 0) text.Append(Digits[jiao]).Append("角");
            else if (fen > 0) text.Append("零");
            if (fen > 0) text.Append(Digits[fen]).Append("分");
            return text.ToString();
        }

        static string UpperGroup(int value)
        {
            var text = new StringBuilder(); bool zero = false;
            for (int position = 3; position >= 0; position--)
            {
                int divisor = (int)Math.Pow(10, position); int digit = value / divisor % 10;
                if (digit == 0) { if (text.Length > 0) zero = true; continue; }
                if (zero) { text.Append("零"); zero = false; }
                text.Append(Digits[digit]).Append(SmallUnits[position]);
            }
            return text.ToString();
        }

    }

    public class CustomerRow { public string Name { get; set; } public string Phone { get; set; } public string Balance { get; set; } public string Status { get; set; } public Customer Customer { get; set; } }
    public class ProductRow { public string FullName { get; set; } public string ShortName { get; set; } public string Unit { get; set; } public string Price { get; set; } public string Status { get; set; } [Browsable(false)] public Product Product { get; set; } }
    public class StatementRow { public int Sequence { get; set; } public string Customer { get; set; } public string Date { get; set; } public string FullName { get; set; } public string ShortName { get; set; } public string Quantity { get; set; } public string Unit { get; set; } public string Price { get; set; } public string Amount { get; set; } public string Note { get; set; } [Browsable(false)] public LedgerEntry Entry { get; set; } [Browsable(false)] public DateTime SortTime { get; set; } [Browsable(false)] public string CreatedUtc { get; set; } [Browsable(false)] public int Line { get; set; } }
    public class CustomerChoice { public string Text { get; set; } public Customer Customer { get; set; } public override string ToString() { return Text; } }
    [Serializable]
    public class SaleDraft { public string CustomerId = ""; public string CustomerText = ""; public string Date = ""; public string EditingSaleId = ""; public List<SaleDraftRow> Rows = new List<SaleDraftRow>(); }
    public class SaleDraftRow { public string FullName { get; set; } public string ShortName { get; set; } public string Unit { get; set; } public string Quantity { get; set; } public string PieceCount { get; set; } public string Price { get; set; } public string Amount { get; set; } public string Note { get; set; } }
    public sealed class SaleValidationException : Exception { public int Row { get; private set; } public int Column { get; private set; } public SaleValidationException(string message, int row, int column) : base(message) { Row = row; Column = column; } }
    public class GitHubRelease { public string tag_name { get; set; } public string body { get; set; } public List<GitHubAsset> assets { get; set; } }
    public class GitHubAsset { public string name { get; set; } public string browser_download_url { get; set; } }

    public sealed class TimeoutWebClient : WebClient
    {
        protected override WebRequest GetWebRequest(Uri address)
        {
            var request = base.GetWebRequest(address); request.Timeout = 30000;
            var http = request as HttpWebRequest; if (http != null) http.ReadWriteTimeout = 30000;
            return request;
        }
    }

    public sealed class DownloadProgressForm : Form
    {
        readonly TimeoutWebClient web; readonly Uri source; readonly string destination;
        readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Top, Height = 28, Minimum = 0, Maximum = 100 };
        readonly Label status = new Label { Dock = DockStyle.Top, Height = 38, Text = "准备下载更新...", TextAlign = ContentAlignment.MiddleLeft };
        bool finished;
        public Exception Error { get; private set; }

        public DownloadProgressForm(TimeoutWebClient web, Uri source, string destination)
        {
            this.web = web; this.source = source; this.destination = destination;
            Text = "下载更新"; Width = 520; Height = 170; Font = new Font("Microsoft YaHei UI", 12F); StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ControlBox = false;
            var cancel = new Button { Text = "取消", AutoSize = true, Dock = DockStyle.Bottom, Height = 38 };
            cancel.Click += delegate { web.CancelAsync(); };
            Controls.Add(cancel); Controls.Add(progress); Controls.Add(status);
            Shown += delegate
            {
                web.DownloadProgressChanged += delegate(object sender, DownloadProgressChangedEventArgs e)
                { progress.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage)); status.Text = "已下载 " + (e.BytesReceived / 1024 / 1024m).ToString("0.0") + " MB / " + (e.TotalBytesToReceive / 1024 / 1024m).ToString("0.0") + " MB"; };
                web.DownloadFileCompleted += delegate(object sender, AsyncCompletedEventArgs e)
                {
                    finished = true; Error = e.Error;
                    if (e.Cancelled || e.Error != null) { try { if (File.Exists(destination)) File.Delete(destination); } catch { } DialogResult = DialogResult.Cancel; }
                    else DialogResult = DialogResult.OK;
                    Close();
                };
                web.DownloadFileAsync(source, destination);
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!finished) web.CancelAsync(); };
        }
    }

    static class SelfTest
    {
        public static void Run()
        {
            string json = "{\"name\":\"测试客户\",\"amount\":12345}";
            byte[] encrypted = LedgerStore.Encrypt(json, "correct-password");
            Assert(LedgerStore.Decrypt(encrypted, "correct-password") == json, "加密往返");
            bool rejected = false; try { LedgerStore.Decrypt(encrypted, "wrong-password"); } catch (CryptographicException) { rejected = true; }
            Assert(rejected, "错误密码必须被拒绝");
            encrypted[40] ^= 1;
            bool tamperRejected = false; try { LedgerStore.Decrypt(encrypted, "correct-password"); } catch (CryptographicException) { tamperRejected = true; }
            Assert(tamperRejected, "被篡改的账本必须被拒绝");
            Assert(Money.Parse("12.34") == 1234 && Money.Text(1234).Replace(",", "") == "12.34", "金额换算");
            var data = new LedgerData(); var customer = new Customer { OpeningCents = 10000 }; data.Customers.Add(customer);
            data.Entries.Add(new LedgerEntry { CustomerId = customer.Id, Kind = "sale", AmountCents = 5000 });
            data.Entries.Add(new LedgerEntry { CustomerId = customer.Id, Kind = "payment", AmountCents = 3000 });
            Assert(LedgerStore.CreateForTest(data).Balance(customer.Id) == 12000, "余额计算");
            var legacy = new LedgerData { SchemaVersion = 1 };
            legacy.Products.Add(new Product { Name = "旧商品", Unit = "件", PriceCents = 500 });
            legacy.Entries.Add(new LedgerEntry { Date = "2026-01-02", Items = null, AmountCents = 500 });
            Assert(LedgerStore.Migrate(legacy) && legacy.SchemaVersion == 2 && legacy.Entries[0].Date == "2026-01-02 00:00" && legacy.Entries[0].Items != null, "v1 到 v2 迁移");
            var items = new List<SaleItem> { new SaleItem { FullName = "CJX2-6511 36V", Quantity = 2, PriceCents = 12500, AmountCents = 25000 }, new SaleItem { FullName = "DZ47-63 C32", PieceCount = 3, PriceCents = 3800, AmountCents = 11400 } };
            Assert(items.Sum(x => x.AmountCents) == 36400, "多商品销售单总额");
            Assert(MainForm.SaleAmount("2", "", "125.00") == 25000 && MainForm.SaleAmount("", "3", "38.00") == 11400, "数量或件数计价");
            Assert(MainForm.SaleItemAmount("2", "", "125.00", "") == 25000 && MainForm.SaleItemAmount("2", "", "125.00", "240.00") == 24000, "自动金额允许手动修改");
            bool invalidSale = false; try { MainForm.SaleAmount("1", "1", "10"); } catch (FormatException) { invalidSale = true; }
            Assert(invalidSale, "数量和件数不能同时计价");
            Assert(SaleExportFormatter.RmbUpper(0) == "零元整", "人民币大写零元");
            Assert(SaleExportFormatter.RmbUpper(100) == "壹元整", "人民币大写整元");
            Assert(SaleExportFormatter.RmbUpper(679215) == "陆仟柒佰玖拾贰元壹角伍分", "人民币大写角分");
            Assert(SaleExportFormatter.RmbUpper(100105) == "壹仟零壹元零伍分", "人民币大写内部零");
            var exportEntry = new LedgerEntry { Date = "2026-09-04 08:06", AmountCents = 36400, Items = items };
            exportEntry.Items[0].Note = "不应导出的备注";
            var export = SaleExportFormatter.Create(new Customer { Name = "张三" }, exportEntry); var exportTable = SaleExportFormatter.Table(export);
            Assert(export.Rows[0].Quantity == 2 && export.Rows[1].Quantity == 3, "模板数量包含数量和件数");
            Assert(export.Period == "2026/9/4" && export.Rows[0].Time == "2026/9/4 8:06", "模板日期格式");
            Assert(exportTable.Columns.Count == 8 && exportTable.Rows.Count == 2 && !exportTable.Rows.SelectMany(x => x).Any(x => Convert.ToString(x).Contains("不应导出的备注")), "销售模板固定八列且不含备注");
            var missing = MainForm.MissingProducts(items.Concat(new[] { items[0] }), new[] { new Product { Name = items[0].FullName } });
            Assert(missing.Count == 1 && missing[0].Name == items[1].FullName && missing[0].PriceCents == items[1].PriceCents, "新商品自动去重");
            string temp = Path.Combine(Path.GetTempPath(), "SuishenLedgerSelfTest_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            try
            {
                string book = Path.Combine(temp, "销售清单.xlsx"); XlsxWriter.Write(book, exportTable);
                using (var archive = ZipFile.OpenRead(book))
                {
                    Assert(archive.GetEntry("xl/worksheets/sheet1.xml") != null && archive.GetEntry("xl/styles.xml") != null, "xlsx 标准结构");
                    string sheet; using (var reader = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml").Open())) sheet = reader.ReadToEnd();
                    Assert(sheet.Contains("商品全名") && sheet.Contains("364") && sheet.Contains("orientation=\"portrait\""), "xlsx 内容与竖向页面");
                }
                var draftStore = LedgerStore.CreateForTest(new LedgerData(), temp); var draft = new SaleDraft { CustomerId = "c1", Date = "2026-09-04 08:06", Rows = new List<SaleDraftRow> { new SaleDraftRow { FullName = "测试商品", Price = "10.00" } } };
                draftStore.SaveDraft(draft); var loadedDraft = draftStore.LoadDraft(); Assert(loadedDraft.CustomerId == "c1" && loadedDraft.Rows[0].FullName == "测试商品", "加密草稿往返");
                draftStore.DeleteDraft(); Assert(!draftStore.HasDraft, "草稿清理");
            }
            finally { Directory.Delete(temp, true); }
            data.Entries[0].Items = items;
            var serializer = new JavaScriptSerializer(); string ledgerJson = serializer.Serialize(data);
            var roundTrip = serializer.Deserialize<LedgerData>(LedgerStore.Decrypt(LedgerStore.Encrypt(ledgerJson, "ledger-password"), "ledger-password"));
            Assert(roundTrip.Entries[0].Items.Count == 2 && roundTrip.Entries[0].Items[1].PieceCount == 3, "多商品加密保存");
            Console.WriteLine("SELF-TEST OK");
        }
        static void Assert(bool value, string name) { if (!value) throw new Exception("SELF-TEST FAILED: " + name); }
    }

    static class UiSmoke
    {
        public static void Run(string output, string tabName)
        {
            var data = new LedgerData { ShopName = "示例百货店" };
            var customer = new Customer { Name = "张三", Phone = "13800000000", OpeningCents = 12000 };
            data.Customers.Add(customer);
            data.Customers.Add(new Customer { Name = "李四", Phone = "13900000000" });
            data.Products.Add(new Product { Name = "CJX2-6511 36V", ShortName = "正泰接触器", Unit = "个", PriceCents = 12500 });
            data.Products.Add(new Product { Name = "DZ47-63 C32", ShortName = "小型断路器", Unit = "件", PriceCents = 3800 });
            data.Entries.Add(new LedgerEntry { CustomerId = customer.Id, Kind = "sale", Date = DateTime.Now.AddHours(-2).ToString("yyyy-MM-dd HH:mm"), Details = "CJX2-6511 36V × 2 个", AmountCents = 25000, Items = new List<SaleItem> { new SaleItem { FullName = "CJX2-6511 36V", ShortName = "正泰接触器", Unit = "个", Quantity = 2, PriceCents = 12500, AmountCents = 25000 } } });
            data.Entries.Add(new LedgerEntry { CustomerId = customer.Id, Kind = "payment", Date = DateTime.Now.AddHours(-1).ToString("yyyy-MM-dd HH:mm"), Details = "现金", AmountCents = 5000 });
            using (var form = new MainForm(LedgerStore.CreateForTest(data), true))
            {
                form.Show(); form.PrepareSmoke(tabName); Application.DoEvents();
                using (var image = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                    image.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                }
                form.Close();
            }
        }
    }

    static class PdfSmoke
    {
        public static ExportTable SampleSale()
        {
            var customer = new Customer { Name = "张三" };
            var entry = new LedgerEntry { Date = "2026-09-04 08:06", AmountCents = 36400, Items = new List<SaleItem> {
                new SaleItem { FullName = "CJX2-6511 36V 长商品型号自动换行测试", ShortName = "正泰接触器", Unit = "个", Quantity = 2, PriceCents = 12500, AmountCents = 25000 },
                new SaleItem { FullName = "DZ47-63 C32", ShortName = "小型断路器", Unit = "件", PieceCount = 3, PriceCents = 3800, AmountCents = 11400 } } };
            return SaleExportFormatter.Table(SaleExportFormatter.Create(customer, entry));
        }

        public static ExportTable SampleStatement()
        {
            var table = new ExportTable { Title = "账单清单", Subject = "全部客户", Period = "2026/8/1 至 2026/9/4", FileName = "全部客户_账单" };
            table.Columns.AddRange(new[] { new ExportColumn("序号", 44, 50, 6, 7, false, true), new ExportColumn("客户", 62, 96, 10, 16), new ExportColumn("时间", 82, 105, 15, 18),
                new ExportColumn("商品全名", 82, 132, 13, 23), new ExportColumn("商品名称", 68, 108, 11, 19), new ExportColumn("数量", 40, 48, 7, 8, false, true),
                new ExportColumn("单位", 38, 46, 6, 7), new ExportColumn("单价", 58, 72, 9, 11, true), new ExportColumn("金额", 58, 72, 10, 12, true), new ExportColumn("备注", 82, 125, 13, 22) });
            for (int i = 1; i <= 28; i++) table.Rows.Add(new object[] { i, i % 2 == 0 ? "张三" : "李四", "2026-09-04 08:06", i % 5 == 0 ? "还款" : "CJX2-6511 36V 长商品型号", i % 5 == 0 ? "现金" : "正泰接触器", i % 5 == 0 ? null : (object)2, i % 5 == 0 ? "" : "个", i % 5 == 0 ? null : (object)125m, i % 5 == 0 ? -50m : 250m, "" });
            table.Summaries.Add(new ExportSummary { Label = "汇总", Text = "期间销售 ¥5,750.00    期间还款 ¥250.00    当前总欠款 ¥5,500.00" }); return table;
        }

        public static void Run(string output, string kind)
        {
            string printer = PrinterSettings.InstalledPrinters.Cast<string>().FirstOrDefault(x => string.Equals(x, "Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase));
            if (printer == null) throw new InvalidOperationException("Microsoft Print to PDF is not installed.");
            string directory = Path.GetDirectoryName(Path.GetFullPath(output)); if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            MainForm.PrintTablePdf(kind == "statement" ? SampleStatement() : SampleSale(), printer, output);
        }
    }
}
