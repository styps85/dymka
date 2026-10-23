using System.Globalization;
using System.Text.RegularExpressions;

namespace Dymka.Core;

public enum FieldKind
{
    None, LastName, FirstName, MiddleName, FullName, BirthDate, Inn, Snils,
    PassportSeries, PassportNumber, PassportSeriesNumber, PassportIssuedBy, PassportIssueDate, PassportCode,
    Phone, Email, Address, Postcode, RegNumber, Sro,
    CardNumber, CardHolder, CardExp, CardExpMonth, CardExpYear, CardCvc,
}

/// <summary>Описание поля формы, которое присылает страница: тип, name, id, autocomplete, подпись, подсказка.</summary>
public sealed record FieldInfo(int Index, string Type, string Name, string Id, string Autocomplete, string Label, string Placeholder)
{
    /// <summary>Ограничение длины поля (maxlength), 0 - нет.</summary>
    public int MaxLength { get; init; }
}

public sealed record FillValue(int Index, string Value);

/// <summary>Распознавание полей форм (русские и английские подписи) и заполнение их данными карточки.</summary>
public static class FormFill
{
    static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // порядок важен: сначала более узкие признаки
    static readonly (FieldKind kind, Regex re)[] Rules =
    {
        // поля поиска не анкета: подсказку карточек в них не показываем
        (FieldKind.None, R(@"поиск|искать|найти|search|query")),
        // поля банковской карты
        (FieldKind.CardCvc, R(@"cvc|cvv|cvn|\bcsc\b|cc-?csc|код\s*(безопасности|проверки|с\s*обратной)|трехзначн|трёхзначн|security\s*code|card\s*code")),
        (FieldKind.CardNumber, R(@"номер\s*(банковской\s*)?карт|card\s*number|cardnumber|cc-?number|cc-?num|\bpan\b|card_?num|credit\s*card")),
        (FieldKind.CardHolder, R(@"владел\w*\s*карт|держател\w*|имя\s*(на|владельца)\s*карт|имя\s*и\s*фамилия\s*на\s*карт|cardholder|card\s*holder|name\s*on\s*card|cc-?name|holder")),
        (FieldKind.CardExpMonth, R(@"exp\w*\s*month|cc-?exp-?month|месяц\s*(окончания|действия)|^мм$|^mm$")),
        (FieldKind.CardExpYear, R(@"exp\w*\s*year|cc-?exp-?year|год\s*(окончания|действия)|^гг(гг)?$|^yy(yy)?$")),
        (FieldKind.CardExp, R(@"срок\s*действ|действительна\s*до|дата\s*окончания|expir|exp\s*date|cc-?exp|valid\s*(thru|until|to)|мм\s*/\s*гг|mm\s*/\s*yy")),
        (FieldKind.Email, R(@"e-?mail|электронн\w*\s*почт|эл\.?\s*почт|\bпочта\b(?!\w)")),
        (FieldKind.Snils, R(@"снилс|snils")),
        (FieldKind.Inn, R(@"(^|[^а-яa-z])инн([^а-яa-z]|$)|(^|[^a-z])inn([^a-z]|$)")),
        (FieldKind.PassportCode, R(@"код\s*подразд|подразделени\w*\s*код|division|kod_?podr|dept_?code|department\s*code")),
        (FieldKind.PassportIssuedBy, R(@"кем\s*выдан|issued\s*by|орган\w*\s*выда|выдавш|issuer")),
        (FieldKind.PassportIssueDate, R(@"дата\s*выдачи|когда\s*выдан|issue_?date|date\s*of\s*issue|issued\s*(on|date)|data_?vyd")),
        (FieldKind.PassportSeriesNumber, R(@"сери\w*\s*и\s*номер|series\s*(and|&)?\s*number|ser_?num")),
        (FieldKind.PassportSeries, R(@"сери[яи]|series|\bser\b|pasp\w*_?ser")),
        (FieldKind.PassportNumber, R(@"номер\s*паспорт|паспорт\w*\s*номер|passport\s*(number|no|num)|pasp\w*_?num|номер\s*документа")),
        (FieldKind.None, R(@"мест\w*\s*рождени|place\s*of\s*birth|birth\s*place")),
        (FieldKind.BirthDate, R(@"рождени|birth|bday|\bdob\b")),
        (FieldKind.FullName, R(@"(^|[^а-я])ф\.?\s*и\.?\s*о\.?(?![а-я])|\bfio\b|full\s*name|fullname|фамилия,?\s*имя|^name$|^your\s*name$")),
        (FieldKind.MiddleName, R(@"отчеств|patronymic|middle|additional-name|second_?name")),
        (FieldKind.LastName, R(@"фамили|surname|last\s*name|lastname|family|last_name")),
        (FieldKind.RegNumber, R(@"рег\w*\.?\s*номер|регистрационн\w*\s*номер|reg_?(num|number|no)\b|номер\s*в\s*реестре")),
        (FieldKind.Sro, R(@"(^|[^а-яa-z])сро([^а-яa-z]|$)|(^|[^a-z])sro([^a-z]|$)|саморегулир")),
        (FieldKind.Postcode, R(@"индекс|postal|post_?code|postcode|zip")),
        (FieldKind.Phone, R(@"телефон|phone|(^|[^a-z])tel([^a-z]|$)|mobile|мобильн|tel-national")),
        (FieldKind.Address, R(@"адрес|address|street|улиц|место\s*жительства|местонахожд")),
        (FieldKind.FirstName, R(@"(^|[^а-я])имя([^а-я]|$)|first\s*name|firstname|given|first_name|\bname_?first")),
    };

    public static FieldKind Classify(FieldInfo f)
    {
        var type = (f.Type ?? "").ToLowerInvariant();
        // поле кода CVC бывает сделано как поле пароля
        if (type == "password")
            return Rules[1].re.IsMatch(((f.Label ?? "") + " " + f.Placeholder + " " + f.Name + " " + f.Id + " " + f.Autocomplete).ToLowerInvariant()) || f.MaxLength is 3 or 4
                ? FieldKind.CardCvc : FieldKind.None;
        if (type is "password" or "hidden" or "checkbox" or "radio" or "submit" or "button" or "file" or "image" or "reset" or "range" or "color")
            return FieldKind.None;
        if (type == "search") return FieldKind.None;
        if (type == "email") return FieldKind.Email;

        // autocomplete - самый надёжный признак, если сайт его указал
        var ac = (f.Autocomplete ?? "").ToLowerInvariant();
        var acKind = ac switch
        {
            _ when ac.Contains("cc-number") => FieldKind.CardNumber,
            _ when ac.Contains("cc-name") => FieldKind.CardHolder,
            _ when ac.Contains("cc-exp-month") => FieldKind.CardExpMonth,
            _ when ac.Contains("cc-exp-year") => FieldKind.CardExpYear,
            _ when ac.Contains("cc-exp") => FieldKind.CardExp,
            _ when ac.Contains("cc-csc") => FieldKind.CardCvc,
            _ when ac.Contains("family-name") => FieldKind.LastName,
            _ when ac.Contains("given-name") => FieldKind.FirstName,
            _ when ac.Contains("additional-name") => FieldKind.MiddleName,
            _ when ac.Contains("bday") => FieldKind.BirthDate,
            _ when ac.Contains("postal-code") => FieldKind.Postcode,
            _ when ac.Contains("street-address") || ac.Contains("address-line") => FieldKind.Address,
            _ when ac.Contains("email") => FieldKind.Email,
            _ when ac.Contains("tel") => FieldKind.Phone,
            _ when Regex.IsMatch(ac, @"(^|\s)name$") => FieldKind.FullName,
            _ => FieldKind.None,
        };
        if (acKind != FieldKind.None) return acKind;

        // подпись и подсказка важнее технических name/id
        foreach (var text in new[] { f.Label, f.Placeholder, f.Name + " " + f.Id })
        {
            var t = (text ?? "").Replace('_', ' ').Replace('-', ' ');
            t = Regex.Replace(t, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant().Trim();
            if (t.Length == 0) continue;
            // в техническом name «e-mail» и «last_name» пишутся с подчёркиваниями - проверяем и исходный вариант
            var raw = (text ?? "").ToLowerInvariant();
            foreach (var (kind, re) in Rules)
                if (re.IsMatch(t) || re.IsMatch(raw)) return kind;
        }
        if (type == "tel") return FieldKind.Phone;   // номер карты тоже бывает type=tel - поэтому проверяется после подписей
        return FieldKind.None;
    }

    /// <summary>Виды всех полей с учётом соседей: «Номер» сразу после «Серии» - номер паспорта.</summary>
    public static FieldKind[] ClassifyAll(IReadOnlyList<FieldInfo> fields)
    {
        var kinds = fields.Select(Classify).ToArray();
        for (int i = 1; i < kinds.Length; i++)
        {
            if (kinds[i] != FieldKind.None || kinds[i - 1] != FieldKind.PassportSeries) continue;
            var t = ((fields[i].Label ?? "") + " " + (fields[i].Placeholder ?? "") + " " + fields[i].Name + " " + fields[i].Id).ToLowerInvariant();
            if (Regex.IsMatch(t, @"номер|number|nomer|\bnum\b|\bno\b")) kinds[i] = FieldKind.PassportNumber;
        }
        return kinds;
    }

    /// <summary>К какому виду адреса относится поле по подписи: «Домашний», «Рабочий», «Почтовый» или null.</summary>
    public static string? AddressKindOf(FieldInfo f)
    {
        var t = ((f.Label ?? "") + " " + (f.Placeholder ?? "") + " " + (f.Name ?? "") + " " + (f.Id ?? "")).ToLowerInvariant();
        if (Regex.IsMatch(t, @"почтов|корреспонд|для\s*писем|postal|mailing|post_?addr")) return "Почтовый";
        if (Regex.IsMatch(t, @"работ|юридическ|места\s*работы|work|office|legal|company")) return "Рабочий";
        if (Regex.IsMatch(t, @"регистрац|прожив|домашн|жительств|прописк|home|residen")) return "Домашний";
        return null;
    }

    static CardAddress? PickAddress(Card card, string? kind, int? chosen)
    {
        if (card.Addresses.Count == 0) return null;
        if (kind != null)
        {
            var byKind = card.Addresses.FirstOrDefault(a => a.Label.Contains(kind[..4], StringComparison.OrdinalIgnoreCase));
            if (byKind != null) return byKind;
            // «Рабочий» и «Юридический» - одно и то же для полей вроде «адрес организации»
            if (kind == "Рабочий")
            {
                var legal = card.Addresses.FirstOrDefault(a => a.Label.Contains("Юрид", StringComparison.OrdinalIgnoreCase));
                if (legal != null) return legal;
            }
        }
        if (chosen is int c && c >= 0 && c < card.Addresses.Count) return card.Addresses[c];
        return card.Addresses[0];
    }

    /// <summary>Дата для поля: у полей type=date формат гггг-мм-дд, у обычных - как в карточке (дд.мм.гггг).</summary>
    public static string DateFor(string type, string value)
    {
        if (!string.Equals(type, "date", StringComparison.OrdinalIgnoreCase)) return value;
        return DateTime.TryParseExact(value.Trim(), new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd")
            : "";
    }

    /// <summary>Значения для полей формы из карточки. chosenAddress - адрес, выбранный вами в подсказке
    /// (используется для полей, по подписи которых не понять, какой адрес нужен).</summary>
    public static List<FillValue> Build(Card card, IReadOnlyList<FieldInfo> fields, int? chosenAddress = null)
    {
        var result = new List<FillValue>();
        // если поле индекса не говорит, к какому адресу относится, берём адрес ближайшего поля адреса перед ним
        CardAddress? lastAddress = null;
        var kinds = ClassifyAll(fields);
        for (int n = 0; n < fields.Count; n++)
        {
            var f = fields[n];
            var kind = kinds[n];
            string v = kind switch
            {
                FieldKind.LastName => card.LastName,
                FieldKind.FirstName => card.FirstName,
                FieldKind.MiddleName => card.MiddleName,
                FieldKind.FullName => card.FullName,
                FieldKind.BirthDate => DateFor(f.Type, card.BirthDate),
                FieldKind.Inn => card.Inn,
                FieldKind.Snils => card.Snils,
                FieldKind.PassportSeries => card.PassportSeries,
                FieldKind.PassportNumber => card.PassportNumber,
                FieldKind.PassportSeriesNumber => (card.PassportSeries + " " + card.PassportNumber).Trim(),
                FieldKind.PassportIssuedBy => card.PassportIssuedBy,
                FieldKind.PassportIssueDate => DateFor(f.Type, card.PassportIssueDate),
                FieldKind.PassportCode => card.PassportCode,
                FieldKind.Phone => card.Phone,
                FieldKind.Email => card.Email,
                FieldKind.RegNumber => card.RegNumber,
                FieldKind.Sro => card.Sro,
                FieldKind.Address => (lastAddress = PickAddress(card, AddressKindOf(f), chosenAddress))?.Text ?? "",
                FieldKind.Postcode => (AddressKindOf(f) is string k ? PickAddress(card, k, chosenAddress) : lastAddress ?? PickAddress(card, null, chosenAddress))?.Postcode ?? "",
                _ => "",
            };
            if (!string.IsNullOrWhiteSpace(v)) result.Add(new FillValue(f.Index, v.Trim()));
        }
        return result;
    }

    public static bool IsBankCardField(FieldKind k) => k is FieldKind.CardNumber or FieldKind.CardHolder or FieldKind.CardExp
        or FieldKind.CardExpMonth or FieldKind.CardExpYear or FieldKind.CardCvc;

    static bool Wants4(FieldInfo f) =>
        Regex.IsMatch(((f.Placeholder ?? "") + " " + (f.Label ?? "")).ToLowerInvariant(), "гггг|yyyy");

    /// <summary>Значения для полей оплаты из банковской карты. Код CVC не подставляется никогда.</summary>
    public static List<FillValue> BuildBankCard(BankCard card, IReadOnlyList<FieldInfo> fields)
    {
        var result = new List<FillValue>();
        var kinds = ClassifyAll(fields);
        for (int n = 0; n < fields.Count; n++)
        {
            var f = fields[n];
            string v = kinds[n] switch
            {
                FieldKind.CardNumber => card.Number,
                FieldKind.CardHolder => card.Holder.ToUpperInvariant(),
                FieldKind.CardExp when card.ExpMonth > 0 => string.Equals(f.Type, "month", StringComparison.OrdinalIgnoreCase)
                    ? $"{card.ExpYear:0000}-{card.ExpMonth:00}"
                    : Wants4(f) || f.MaxLength == 7 ? $"{card.ExpMonth:00}/{card.ExpYear:0000}" : $"{card.ExpMonth:00}/{card.ExpYear % 100:00}",
                FieldKind.CardExpMonth when card.ExpMonth > 0 => $"{card.ExpMonth:00}",
                FieldKind.CardExpYear when card.ExpYear > 0 => f.Type == "select" || Wants4(f) || f.MaxLength == 4 ? $"{card.ExpYear:0000}" : $"{card.ExpYear % 100:00}",
                _ => "",
            };
            if (v.Length > 0) result.Add(new FillValue(f.Index, v));
        }
        return result;
    }

    /// <summary>Поле кода CVC (после подстановки карты курсор ставится туда).</summary>
    public static int CvcIndex(IReadOnlyList<FieldInfo> fields)
    {
        var kinds = ClassifyAll(fields);
        for (int n = 0; n < fields.Count; n++) if (kinds[n] == FieldKind.CardCvc) return fields[n].Index;
        return -1;
    }

    public static string KindName(FieldKind k) => k switch
    {
        FieldKind.LastName => "фамилия",
        FieldKind.FirstName => "имя",
        FieldKind.MiddleName => "отчество",
        FieldKind.FullName => "ФИО",
        FieldKind.BirthDate => "дата рождения",
        FieldKind.Inn => "ИНН",
        FieldKind.Snils => "СНИЛС",
        FieldKind.PassportSeries => "серия паспорта",
        FieldKind.PassportNumber => "номер паспорта",
        FieldKind.PassportSeriesNumber => "серия и номер паспорта",
        FieldKind.PassportIssuedBy => "кем выдан",
        FieldKind.PassportIssueDate => "дата выдачи",
        FieldKind.PassportCode => "код подразделения",
        FieldKind.Phone => "телефон",
        FieldKind.Email => "почта",
        FieldKind.Address => "адрес",
        FieldKind.Postcode => "индекс",
        FieldKind.RegNumber => "рег. номер",
        FieldKind.Sro => "СРО",
        _ => "",
    };
}
