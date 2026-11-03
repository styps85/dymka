using System.Text;

namespace Dymka.Core;

/// <summary>Отбор вариантов подсказки у поля по тому, что вы начали вводить: остаются варианты,
/// в которых есть каждое введённое слово (или его часть), без учёта регистра и разницы «е»/«ё».</summary>
public static class FillFilter
{
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (var ch0 in s)
        {
            var ch = char.ToLowerInvariant(ch0);
            if (ch == 'ё') ch = 'е';
            if (char.IsWhiteSpace(ch) || ch == ' ')
            {
                if (sb.Length > 0) space = true;
                continue;
            }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Пустой запрос подходит ко всему. Иначе каждое слово запроса должно встречаться в тексте варианта;
    /// цифры сравниваются и без пробелов и дефисов (ИНН, СНИЛС, телефон, номер карты).</summary>
    public static bool Matches(string? query, string? haystack)
    {
        var q = Normalize(query);
        if (q.Length == 0) return true;
        var h = Normalize(haystack);
        // цифры сравниваются внутри каждого поля карточки (поля разделены «|»), чтобы не склеивать ИНН с СНИЛС
        var digits = h.Split('|').Select(DigitsOnly).Where(d => d.Length > 0).ToList();
        foreach (var word in q.Split(' '))
        {
            if (h.Contains(word, StringComparison.Ordinal)) continue;
            var d = DigitsOnly(word);
            if (d.Length > 0 && word.All(c => char.IsDigit(c) || c is '-' or '(' or ')' or '+' or '.') && digits.Any(x => x.Contains(d, StringComparison.Ordinal))) continue;
            return false;
        }
        return true;
    }

    static string DigitsOnly(string s) => new(s.Where(char.IsDigit).ToArray());

    /// <summary>Текст карточки для поиска: название, ФИО, ИНН, СНИЛС, паспорт, контакты, адреса.</summary>
    public static string SearchText(Card c) => string.Join(" | ", new[]
    {
        c.Title, c.FullName, c.ShortName, c.Inn, c.Snils, c.BirthDate, c.PassportSeries + c.PassportNumber,
        c.PassportSeries + " " + c.PassportNumber, c.Phone, c.Email, c.RegNumber, c.Sro,
    }.Concat(c.Addresses.SelectMany(a => new[] { a.Label, a.Postcode, a.Text })));

    public static string SearchText(BankCard c) => string.Join(" | ", c.Title, c.Holder, c.Bank, c.Note, c.Last4);

    public static string SearchText(PasswordEntry e) => string.Join(" | ", e.Username, e.Host, e.Note);
}
