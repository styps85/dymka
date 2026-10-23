using System.Text;

namespace Dymka.Core;

/// <summary>Импорт паролей из файла CSV, выгруженного из Chrome, Яндекс Браузера, Edge, Firefox и др.</summary>
public static class PasswordCsv
{
    static readonly string[] UrlCols = { "url", "origin", "website", "web site", "login_uri", "адрес", "сайт", "origin_url", "uri" };
    static readonly string[] UserCols = { "username", "login", "user", "логин", "имя пользователя", "login_username", "email" };
    static readonly string[] PassCols = { "password", "пароль", "login_password" };
    static readonly string[] NoteCols = { "note", "notes", "comment", "примечание" };
    static readonly string[] NameCols = { "name", "title", "название" };

    public static List<List<string>> ParseRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        text = text.TrimStart('﻿');
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(cell.ToString()); cell.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = new List<string>();
                    break;
                default: cell.Append(c); break;
            }
        }
        row.Add(cell.ToString());
        if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
        return rows;
    }

    static int Find(List<string> header, string[] names)
    {
        for (int i = 0; i < header.Count; i++)
            if (names.Contains(header[i].Trim().ToLowerInvariant())) return i;
        return -1;
    }

    /// <summary>Записи из CSV и сколько строк пропущено (нет адреса сайта или пароля, приложения Android и т.п.).</summary>
    public static (List<PasswordEntry> entries, int skipped, string? error) Parse(string text)
    {
        var rows = ParseRows(text);
        if (rows.Count == 0) return (new(), 0, "файл пустой");
        var header = rows[0];
        int u = Find(header, UrlCols), l = Find(header, UserCols), p = Find(header, PassCols), n = Find(header, NoteCols), nm = Find(header, NameCols);
        if (u < 0 || p < 0) return (new(), 0, "в файле нет столбцов с адресом сайта и паролем - это не выгрузка паролей браузера");
        var list = new List<PasswordEntry>();
        int skipped = 0;
        foreach (var r in rows.Skip(1))
        {
            string Get(int i) => i >= 0 && i < r.Count ? r[i].Trim() : "";
            string Raw(int k) => k >= 0 && k < r.Count ? r[k] : "";   // пароль - без обрезки пробелов
            var url = Get(u);
            var pass = Raw(p);
            if (url.Length == 0 || pass.Length == 0 || url.StartsWith("android://", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
            if (!url.Contains("://")) url = "https://" + url;
            var host = UrlTools.HostOf(url).ToLowerInvariant();
            if (host.Length == 0) { skipped++; continue; }
            var note = Get(n);
            var name = Get(nm);
            if (name.Length > 0 && !name.Equals(host, StringComparison.OrdinalIgnoreCase) && note.Length == 0 && !host.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                note = name;
            list.Add(new PasswordEntry { Url = url, Host = host, Username = Get(l), Password = pass, Note = note });
        }
        return (list, skipped, null);
    }

    /// <summary>Добавляет записи в хранилище: новые добавляются, у существующих (тот же сайт и логин) обновляется пароль.</summary>
    public static (int added, int updated, int same) Merge(VaultData data, IEnumerable<PasswordEntry> entries)
    {
        int added = 0, updated = 0, same = 0;
        foreach (var e in entries)
        {
            var ex = data.Passwords.FirstOrDefault(x => x.Host == e.Host && x.Username == e.Username);
            if (ex == null) { data.Passwords.Add(e); added++; }
            else if (ex.Password != e.Password) { ex.Password = e.Password; ex.Updated = DateTime.Now; updated++; }
            else same++;
        }
        return (added, updated, same);
    }
}
