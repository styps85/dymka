using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Dymka.Core;

namespace Dymka;

/// <summary>Окно «Пароли и автозаполнение»: пароли, карточки (с несколькими адресами), защита.</summary>
public partial class VaultWindow : Window
{
    readonly Vault _vault;
    readonly AppSettings _s;
    readonly MainWindow _main;
    PasswordEntry? _pw;
    Card? _card;
    readonly List<(Func<Card, string> get, Action<Card, string> set, TextBox box)> _cardFields = new();
    TextBox? _cardTitle;
    StackPanel? _addrPanel;

    static readonly string[] AddressLabels = { "Домашний", "Рабочий", "Почтовый", "Юридический" };

    public VaultWindow(Vault vault, AppSettings settings, string page, MainWindow main)
    {
        _vault = vault;
        _s = settings;
        _main = main;
        InitializeComponent();
        BuildCardEditor();
        foreach (var (m, text) in new[] { (5, "5 минут"), (15, "15 минут"), (60, "1 час"), (0, "Только при закрытии браузера") })
        {
            var rb = new RadioButton { Content = text, Tag = m, GroupName = "lock", Style = (Style)FindResource("Chip"), IsChecked = m == _s.VaultLockMinutes };
            rb.Checked += (_, _) => { _s.VaultLockMinutes = (int)rb.Tag; _s.Save(); };
            LockPanel.Children.Add(rb);
        }
        OfferSaveBox.IsChecked = _s.OfferSavePasswords;
        OfferFillBox.IsChecked = _s.OfferAutofill;
        RefreshPasswords();
        RefreshCards();
        RefreshSecurity();
        RefreshBank();
        (page == "cards" ? TabCards : page == "bankcards" ? TabBank : page == "security" ? TabSecurity : TabPasswords).IsChecked = true;
    }

    VaultData Data => _vault.Data ?? throw new InvalidOperationException("Хранилище закрыто");

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PagePasswords == null) return;
        PagePasswords.Visibility = TabPasswords.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageCards.Visibility = TabCards.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageBank.Visibility = TabBank.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSecurity.Visibility = TabSecurity.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowPageForTest(string page) =>
        (page == "cards" ? TabCards : page == "bankcards" ? TabBank : page == "security" ? TabSecurity : TabPasswords).IsChecked = true;

    // ===================================================================== пароли

    void RefreshPasswords(PasswordEntry? select = null)
    {
        var q = PwSearch.Text.Trim();
        var list = Data.Passwords
            .Where(p => q.Length == 0 || p.Host.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Username.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Host).ThenBy(p => p.Username).ToList();
        PwList.ItemsSource = list;
        PwEmpty.Visibility = Data.Passwords.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (select != null && list.Contains(select)) PwList.SelectedItem = select;
        else if (!list.Contains(_pw!)) ShowPassword(null);
    }

    void PwSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PwSearchHint != null) PwSearchHint.Visibility = PwSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (PwList != null) RefreshPasswords();
    }

    void PwList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PwList.SelectedItem is PasswordEntry p) ShowPassword(p);
    }

    void ShowPassword(PasswordEntry? p)
    {
        _pw = p;
        PwEditor.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        PwNone.Visibility = p == null ? Visibility.Visible : Visibility.Collapsed;
        if (p == null) return;
        PwUrl.Text = p.Url.Length > 0 ? p.Url : "https://" + p.Host;
        PwUser.Text = p.Username;
        PwPass.Password = p.Password;
        PwPassPlain.Text = p.Password;
        PwNote.Text = p.Note;
        SetShown(false);
        PwInfo.Text = $"Сохранён {p.Created:dd.MM.yyyy}" + (p.Updated > p.Created.AddMinutes(1) ? $", изменён {p.Updated:dd.MM.yyyy}" : "") +
                      (p.LastUsed > DateTime.MinValue ? $", подставлялся {p.LastUsed:dd.MM.yyyy}" : "");
    }

    bool _shown;
    void SetShown(bool shown)
    {
        if (_shown && !shown) PwPass.Password = PwPassPlain.Text;
        if (!_shown && shown) PwPassPlain.Text = PwPass.Password;
        _shown = shown;
        PwPass.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
        PwPassPlain.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        PwShowBtn.Content = shown ? "Скрыть" : "Показать";
    }

    string CurrentPassword => _shown ? PwPassPlain.Text : PwPass.Password;

    void PwShow_Click(object sender, RoutedEventArgs e) => SetShown(!_shown);

    void PwCopyUser_Click(object sender, RoutedEventArgs e) => Copy(PwUser.Text, "Логин скопирован");

    void PwCopyPass_Click(object sender, RoutedEventArgs e)
    {
        Copy(CurrentPassword, "Пароль скопирован");
        if (_pw != null) { _pw.LastUsed = DateTime.Now; _vault.Save(); }
    }

    void Copy(string text, string done)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); _main.ShowToast(done); return; }
            catch { Thread.Sleep(60); }
        }
    }

    void PwNew_Click(object sender, RoutedEventArgs e)
    {
        // новая запись попадает в хранилище только после «Сохранить»
        var p = new PasswordEntry { Url = "https://", Host = "" };
        PwList.SelectedItem = null;
        ShowPassword(p);
        PwUrl.Focus();
        PwUrl.CaretIndex = PwUrl.Text.Length;
    }

    void PwSave_Click(object sender, RoutedEventArgs e)
    {
        if (_pw == null) return;
        var url = PwUrl.Text.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        var host = UrlTools.HostOf(url).ToLowerInvariant();
        if (host.Length == 0)
        {
            Dialogs.Show(this, "Не указан сайт", "Введите адрес сайта, например kad.arbitr.ru.", new[] { "Хорошо" });
            return;
        }
        _pw.Url = url;
        _pw.Host = host;
        _pw.Username = PwUser.Text.Trim();
        if (_pw.Password != CurrentPassword) { _pw.Password = CurrentPassword; _pw.Updated = DateTime.Now; }
        _pw.Note = PwNote.Text.Trim();
        if (!Data.Passwords.Contains(_pw)) Data.Passwords.Add(_pw);
        _vault.Save();
        RefreshPasswords(_pw);
        _main.ShowToast("Пароль сохранён");
    }

    void PwDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_pw == null) return;
        if (!Data.Passwords.Contains(_pw)) { ShowPassword(null); return; }
        var (i, _) = Dialogs.Show(this, "Удалить пароль?", $"Пароль для {(_pw.Host.Length > 0 ? _pw.Host : "сайта")} ({_pw.Username}) будет удалён.", new[] { "Удалить", "Отмена" }, defaultIndex: 1);
        if (i != 0) return;
        Data.Passwords.Remove(_pw);
        _vault.Save();
        ShowPassword(null);
        RefreshPasswords();
    }

    void PwImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Файл с паролями (CSV) из Chrome, Яндекс Браузера, Edge или Firefox",
            Filter = "Файлы CSV (*.csv)|*.csv|Все файлы (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        var r = ImportCsv(dlg.FileName);
        if (r.error != null)
        {
            Dialogs.Show(this, "Не удалось импортировать", r.error, new[] { "Закрыть" });
            return;
        }
        var (i, _) = Dialogs.Show(this, "Пароли перенесены",
            $"Новых паролей: {r.added}, обновлено: {r.updated}, уже были: {r.same}" + (r.skipped > 0 ? $", пропущено строк: {r.skipped}" : "") + ".\n\n" +
            "В файле CSV пароли записаны открытым текстом - любой, кто его откроет, их увидит. Удалить файл?",
            new[] { "Удалить файл", "Оставить" });
        if (i == 0) DeleteFileSecurely(dlg.FileName);
    }

    /// <summary>Импорт CSV (для кнопки и самопроверки).</summary>
    public (int added, int updated, int same, int skipped, string? error) ImportCsv(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { return (0, 0, 0, 0, "Файл не открывается: " + ex.Message); }
        var (entries, skipped, error) = PasswordCsv.Parse(text);
        if (error != null) return (0, 0, 0, skipped, char.ToUpper(error[0]) + error[1..] + ".");
        var (a, u, s) = PasswordCsv.Merge(Data, entries);
        _vault.Save();
        RefreshPasswords();
        App.Log($"Импорт паролей: новых {a}, обновлено {u}, без изменений {s}, пропущено {skipped}");
        return (a, u, s, skipped, null);
    }

    /// <summary>Файл с паролями перед удалением перезаписывается нулями.</summary>
    public static bool DeleteFileSecurely(string path)
    {
        try
        {
            var len = new FileInfo(path).Length;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                var zeros = new byte[Math.Min(len, 65536)];
                for (long done = 0; done < len; done += zeros.Length) fs.Write(zeros, 0, (int)Math.Min(zeros.Length, len - done));
            }
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            App.Log("Удаление файла CSV: " + ex.Message);
            return false;
        }
    }

    // ===================================================================== карточки

    void BuildCardEditor()
    {
        var p = CardEditor;
        TextBlock Section(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 16, 0, 6) };
        TextBox Field(string label, Panel into, double width = double.NaN)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 10, 8), Width = width };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 4) });
            var tb = new TextBox { Style = (Style)FindResource("FieldBox") };
            sp.Children.Add(tb);
            into.Children.Add(sp);
            return tb;
        }
        void Add(string label, Func<Card, string> get, Action<Card, string> set, Panel into, double width = double.NaN) =>
            _cardFields.Add((get, set, Field(label, into, width)));
        UniformGrid Row(int cols) { var g = new UniformGrid { Columns = cols }; p.Children.Add(g); return g; }

        var titleRow = new StackPanel();
        p.Children.Add(titleRow);
        _cardTitle = Field("Название карточки (например: Я, АУ, Иванов И.И.)", titleRow);

        p.Children.Add(Section("ЧЕЛОВЕК"));
        var r1 = Row(3);
        Add("Фамилия", c => c.LastName, (c, v) => c.LastName = v, r1);
        Add("Имя", c => c.FirstName, (c, v) => c.FirstName = v, r1);
        Add("Отчество", c => c.MiddleName, (c, v) => c.MiddleName = v, r1);
        var r2 = Row(3);
        Add("Дата рождения (дд.мм.гггг)", c => c.BirthDate, (c, v) => c.BirthDate = v, r2);
        Add("ИНН", c => c.Inn, (c, v) => c.Inn = v, r2);
        Add("СНИЛС", c => c.Snils, (c, v) => c.Snils = v, r2);

        p.Children.Add(Section("ПАСПОРТ"));
        var r3 = Row(3);
        Add("Серия", c => c.PassportSeries, (c, v) => c.PassportSeries = v, r3);
        Add("Номер", c => c.PassportNumber, (c, v) => c.PassportNumber = v, r3);
        Add("Дата выдачи", c => c.PassportIssueDate, (c, v) => c.PassportIssueDate = v, r3);
        var r4 = new DockPanel();
        p.Children.Add(r4);
        var codeHost = new StackPanel();
        DockPanel.SetDock(codeHost, Dock.Right);
        r4.Children.Add(codeHost);
        Add("Код подразделения", c => c.PassportCode, (c, v) => c.PassportCode = v, codeHost, 150);
        var issuedHost = new StackPanel();
        r4.Children.Add(issuedHost);
        Add("Кем выдан", c => c.PassportIssuedBy, (c, v) => c.PassportIssuedBy = v, issuedHost);

        p.Children.Add(Section("КОНТАКТЫ"));
        var r5 = Row(2);
        Add("Телефон", c => c.Phone, (c, v) => c.Phone = v, r5);
        Add("Электронная почта", c => c.Email, (c, v) => c.Email = v, r5);

        p.Children.Add(Section("АРБИТРАЖНЫЙ УПРАВЛЯЮЩИЙ (если нужно)"));
        var r6 = Row(2);
        Add("Регистрационный номер", c => c.RegNumber, (c, v) => c.RegNumber = v, r6);
        Add("СРО", c => c.Sro, (c, v) => c.Sro = v, r6);

        p.Children.Add(Section("АДРЕСА"));
        p.Children.Add(new TextBlock
        {
            Text = "Если в анкете написано «адрес регистрации», «почтовый адрес» или «адрес работы», подставится подходящий адрес. Для поля просто «Адрес» адрес можно выбрать в подсказке.",
            FontSize = 12, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        _addrPanel = new StackPanel();
        p.Children.Add(_addrPanel);
        var addBtn = new Button { Content = "Добавить адрес", Style = (Style)FindResource("SecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
        addBtn.Click += (_, _) =>
        {
            var used = AddressRows().Select(r => r.label.Text).ToHashSet();
            AddAddressRow(new CardAddress { Label = AddressLabels.FirstOrDefault(l => !used.Contains(l)) ?? "Другой" });
        };
        p.Children.Add(addBtn);
    }

    void AddAddressRow(CardAddress a)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var label = new ComboBox { IsEditable = true, Width = 140, Height = 38, FontSize = 13.5, VerticalContentAlignment = VerticalAlignment.Center, Text = a.Label, ToolTip = "Вид адреса" };
        foreach (var l in AddressLabels) label.Items.Add(l);
        label.Text = a.Label;
        var post = new TextBox { Style = (Style)FindResource("FieldBox"), Width = 90, Text = a.Postcode, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Индекс" };
        var text = new TextBox { Style = (Style)FindResource("FieldBox"), Text = a.Text, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Адрес" };
        var del = new Button { Style = (Style)FindResource("SmallIconButton"), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Удалить адрес", Content = new Glyph { Data = (Geometry)FindResource("IcClose"), Size = 13, StrokeWidth = 2.4 } };
        del.Click += (_, _) => _addrPanel!.Children.Remove(row);
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(post, Dock.Left);
        DockPanel.SetDock(del, Dock.Right);
        row.Children.Add(label);
        row.Children.Add(post);
        row.Children.Add(del);
        row.Children.Add(text);
        row.Tag = (label, post, text);
        _addrPanel!.Children.Add(row);
    }

    IEnumerable<(ComboBox label, TextBox post, TextBox text)> AddressRows() =>
        _addrPanel!.Children.OfType<DockPanel>().Select(r => ((ComboBox, TextBox, TextBox))r.Tag);

    void RefreshCards(Card? select = null)
    {
        CardList.ItemsSource = null;
        CardList.ItemsSource = Data.Cards.ToList();
        CardEmpty.Visibility = Data.Cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (select != null) CardList.SelectedItem = select;
        else if (_card == null || !Data.Cards.Contains(_card)) ShowCard(null);
    }

    void CardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CardList.SelectedItem is Card c && c != _card) ShowCard(c);
    }

    void ShowCard(Card? c)
    {
        _card = c;
        CardEditorHost.Visibility = c == null ? Visibility.Collapsed : Visibility.Visible;
        CardNone.Visibility = c == null ? Visibility.Visible : Visibility.Collapsed;
        if (c == null) return;
        _cardTitle!.Text = c.Title;
        foreach (var (get, _, box) in _cardFields) box.Text = get(c);
        _addrPanel!.Children.Clear();
        foreach (var a in c.Addresses) AddAddressRow(a);
        if (c.Addresses.Count == 0) AddAddressRow(new CardAddress { Label = "Домашний" });
    }

    void CardNew_Click(object sender, RoutedEventArgs e)
    {
        string title = Data.Cards.Count switch { 0 => "Я", 1 => "АУ", _ => "Должник" };
        var c = new Card { Title = title };
        Data.Cards.Add(c);
        _vault.Save();
        RefreshCards(c);
        ShowCard(c);
        _cardTitle!.Focus();
        _cardTitle.SelectAll();
    }

    public void SaveCardFromEditor()
    {
        if (_card == null) return;
        _card.Title = string.IsNullOrWhiteSpace(_cardTitle!.Text) ? "Карточка" : _cardTitle.Text.Trim();
        foreach (var (_, set, box) in _cardFields) set(_card, box.Text.Trim());
        _card.Addresses = AddressRows()
            .Where(r => r.text.Text.Trim().Length > 0 || r.post.Text.Trim().Length > 0)
            .Select(r => new CardAddress { Label = string.IsNullOrWhiteSpace(r.label.Text) ? "Адрес" : r.label.Text.Trim(), Postcode = r.post.Text.Trim(), Text = r.text.Text.Trim() })
            .ToList();
        _vault.Save();
        RefreshCards(_card);
    }

    void CardSave_Click(object sender, RoutedEventArgs e)
    {
        SaveCardFromEditor();
        _main.ShowToast("Карточка сохранена");
    }

    void CardDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_card == null) return;
        var (i, _) = Dialogs.Show(this, "Удалить карточку?", $"Карточка «{_card.Title}» будет удалена.", new[] { "Удалить", "Отмена" }, defaultIndex: 1);
        if (i != 0) return;
        Data.Cards.Remove(_card);
        _vault.Save();
        _card = null;
        RefreshCards();
    }

    /// <summary>Для самопроверки: заполнить редактор карточки.</summary>
    public void FillCardEditorForTest(Card c)
    {
        CardNew_Click(this, new RoutedEventArgs());
        _cardTitle!.Text = c.Title;
        foreach (var (get, _, box) in _cardFields) box.Text = get(c);
        _addrPanel!.Children.Clear();
        foreach (var a in c.Addresses) AddAddressRow(a);
    }

    // ===================================================================== банковские карты

    BankCard? _bc;

    void RefreshBank(BankCard? select = null)
    {
        BcList.ItemsSource = null;
        BcList.ItemsSource = Data.BankCards.ToList();
        BcEmpty.Visibility = Data.BankCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (select != null) BcList.SelectedItem = select;
        else if (_bc == null || !Data.BankCards.Contains(_bc)) ShowBank(null);
    }

    void BcList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BcList.SelectedItem is BankCard c && c != _bc) ShowBank(c);
    }

    void ShowBank(BankCard? c)
    {
        _bc = c;
        BcEditor.Visibility = c == null ? Visibility.Collapsed : Visibility.Visible;
        BcNone.Visibility = c == null ? Visibility.Visible : Visibility.Collapsed;
        if (c == null) return;
        BcTitle.Text = c.Title;
        BcNumber.Text = CardTools.Format(c.Number);
        BcHolder.Text = c.Holder;
        BcExp.Text = c.ExpText;
        BcNote.Text = c.Note;
        UpdateNumberInfo();
    }

    void UpdateNumberInfo()
    {
        var d = CardTools.Digits(BcNumber.Text);
        BcNumberInfo.Text = d.Length == 0 ? ""
            : !CardTools.Luhn(d) ? "Проверьте номер: похоже, в нём опечатка"
            : CardTools.Brand(d) is { Length: > 0 } b ? "Платёжная система: " + b : "";
        BcNumberInfo.Foreground = d.Length > 0 && !CardTools.Luhn(d) ? new SolidColorBrush(Color.FromRgb(0xC2, 0x35, 0x2E)) : (Brush)FindResource("Muted");
    }

    void BcNumber_LostFocus(object sender, RoutedEventArgs e)
    {
        var d = CardTools.Digits(BcNumber.Text);
        if (d.Length > 0) BcNumber.Text = CardTools.Format(d);
        UpdateNumberInfo();
    }

    void BcNew_Click(object sender, RoutedEventArgs e)
    {
        var c = new BankCard { Title = "Карта" };
        BcList.SelectedItem = null;
        ShowBank(c);
        BcTitle.Focus();
        BcTitle.SelectAll();
    }

    /// <summary>Сохранить карту из редактора. false - есть ошибка, о которой сказано.</summary>
    public bool SaveBankFromEditor(bool askOnTypo = true)
    {
        if (_bc == null) return false;
        var d = CardTools.Digits(BcNumber.Text);
        if (d.Length < 12 || d.Length > 19)
        {
            Dialogs.Show(this, "Номер карты", "Номер карты - от 12 до 19 цифр.", new[] { "Хорошо" });
            return false;
        }
        if (!CardTools.Luhn(d) && askOnTypo)
        {
            var (i, _) = Dialogs.Show(this, "Похоже на опечатку", "Контрольная сумма номера не сходится - обычно это значит, что в номере ошибка.", new[] { "Исправить", "Сохранить как есть" });
            if (i == 0) return false;
        }
        int m = 0, y = 0;
        if (BcExp.Text.Trim().Length > 0 && !CardTools.TryParseExp(BcExp.Text, out m, out y))
        {
            Dialogs.Show(this, "Срок действия", "Укажите срок как на карте, например 05/28.", new[] { "Хорошо" });
            return false;
        }
        _bc.Title = string.IsNullOrWhiteSpace(BcTitle.Text) ? (CardTools.Brand(d) + " •••• " + d[^4..]).Trim() : BcTitle.Text.Trim();
        _bc.Number = d;
        _bc.Holder = BcHolder.Text.Trim().ToUpperInvariant();
        _bc.ExpMonth = m;
        _bc.ExpYear = y;
        _bc.Note = BcNote.Text.Trim();
        if (!Data.BankCards.Contains(_bc)) Data.BankCards.Add(_bc);
        _vault.Save();
        RefreshBank(_bc);
        return true;
    }

    void BcSave_Click(object sender, RoutedEventArgs e)
    {
        if (SaveBankFromEditor()) _main.ShowToast("Карта сохранена");
    }

    void BcCopy_Click(object sender, RoutedEventArgs e) => Copy(CardTools.Digits(BcNumber.Text), "Номер карты скопирован");

    void BcDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_bc == null) return;
        if (!Data.BankCards.Contains(_bc)) { ShowBank(null); return; }
        var (i, _) = Dialogs.Show(this, "Удалить карту?", $"Карта «{_bc.Title}» ({_bc.Masked}) будет удалена.", new[] { "Удалить", "Отмена" }, defaultIndex: 1);
        if (i != 0) return;
        Data.BankCards.Remove(_bc);
        _vault.Save();
        _bc = null;
        RefreshBank();
    }

    /// <summary>Для самопроверки: заполнить редактор карты.</summary>
    public void FillBankEditorForTest(string title, string number, string holder, string exp)
    {
        BcNew_Click(this, new RoutedEventArgs());
        BcTitle.Text = title;
        BcNumber.Text = number;
        BcHolder.Text = holder;
        BcExp.Text = exp;
    }

    // ===================================================================== защита

    void RefreshSecurity()
    {
        bool has = _vault.HasMasterPassword;
        MasterInfo.Text = has
            ? "Главный пароль задан: после простоя и при блокировке Windows хранилище закрывается, и чтобы подставить пароль, нужно ввести главный пароль."
            : "Главный пароль не задан: хранилище открывается само под вашей учётной записью Windows. Главный пароль добавляет защиту на случай, если за компьютер сядет другой человек, пока вы не заблокировали Windows.";
        MasterSetBtn.Content = has ? "Сменить главный пароль" : "Задать главный пароль";
        MasterRemoveBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        LockNowBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        LockLabel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        LockPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    void MasterSet_Click(object sender, RoutedEventArgs e)
    {
        if (_vault.HasMasterPassword)
        {
            var old = Dialogs.AskPassword(this, "Сменить главный пароль", "Сначала введите текущий главный пароль.", confirm: false,
                validate: p => _vault.CheckMasterPassword(p) ? null : "Неверный главный пароль");
            if (old == null) return;
        }
        var pw = Dialogs.AskPassword(this, _vault.HasMasterPassword ? "Новый главный пароль" : "Главный пароль",
            "Придумайте пароль не короче 8 символов. Если вы его забудете, сохранённые пароли и карточки восстановить будет нельзя.", confirm: true,
            validate: p => p.Length < 8 ? "Слишком короткий пароль: нужно не меньше 8 символов" : null);
        if (pw == null) return;
        Cursor = System.Windows.Input.Cursors.Wait;
        try { _vault.SetMasterPassword(pw); }
        finally { Cursor = null; }
        RefreshSecurity();
        _main.ShowToast("Главный пароль задан");
    }

    void MasterRemove_Click(object sender, RoutedEventArgs e)
    {
        var old = Dialogs.AskPassword(this, "Убрать главный пароль", "Введите текущий главный пароль. Хранилище останется зашифрованным и будет защищено вашей учётной записью Windows.",
            confirm: false, validate: p => _vault.CheckMasterPassword(p) ? null : "Неверный главный пароль");
        if (old == null) return;
        _vault.SetMasterPassword(null);
        RefreshSecurity();
        _main.ShowToast("Главный пароль убран");
    }

    void LockNow_Click(object sender, RoutedEventArgs e)
    {
        _vault.Lock();
        _main.ShowToast("Хранилище закрыто");
        Close();
    }

    void Offer_Click(object sender, RoutedEventArgs e)
    {
        _s.OfferSavePasswords = OfferSaveBox.IsChecked == true;
        _s.OfferAutofill = OfferFillBox.IsChecked == true;
        _s.Save();
    }
}
