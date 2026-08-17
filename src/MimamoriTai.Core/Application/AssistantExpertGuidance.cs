using System.Globalization;
using System.Text;

namespace MimamoriTai.Core.Application;

/// <summary>Which profession the question really belongs to.</summary>
public enum ExpertField
{
    /// <summary>Medicine, prescriptions, symptoms, diagnoses.</summary>
    Medical = 0,

    /// <summary>Long-term care certification, care managers, facilities.</summary>
    Care = 1,

    /// <summary>Pensions, inheritance, contracts, anything with money or law in it.</summary>
    Money = 2
}

/// <summary>A question this assistant must not answer itself, and who should answer it.</summary>
public sealed record ExpertReferral(ExpertField Field, string Reply);

/// <summary>
/// The questions where being helpful is the failure mode.
///
/// "この薬とこの薬を一緒に飲んでいい?" has a correct answer, and a language model will
/// happily produce one. If it is wrong, an 85 year old takes the wrong dose on the
/// authority of a service their family installed for them. The same holds for 要介護
/// certification, pensions and inheritance: a confident wrong answer costs money or care
/// the resident cannot get back.
///
/// So these are never sent to a model at all. Detection is keyword based, which means it
/// still works while the AI router is down — exactly when a fallback answer would
/// otherwise be improvised. The language-model router (<see cref="AssistantTopic.Expert"/>)
/// catches the long tail this list does not name.
///
/// The tone matters as much as the refusal. "お答えできません" alone reads as being turned
/// away, so every reply receives the worry first, then names a specific person to ask, then
/// leaves the resident with something they can actually do next.
/// </summary>
public static class AssistantExpertGuidance
{
    /// <summary>
    /// Inner arrays are AND, outer arrays are OR, matching the knowledge base.
    ///
    /// Deliberately narrow. Every entry is one where a wrong answer does harm, so a false
    /// positive is the cheaper mistake — but not free. 「薬を飲みました」 is a resident
    /// reporting their day, and answering that with "わたしからはお答えしません" turns a
    /// small moment of contact into a rebuff, so the bare word 薬 has to be paired with
    /// wording that asks something. The words that are only ever questions (処方, 飲み合わせ,
    /// 副作用, 要介護, 相続 …) stand alone.
    ///
    /// Broad words like 病院 and 体調 are left out entirely: 「病院に行ってきた」 is
    /// conversation, and 「体調が悪い」 already has its own caring answer plus a button that
    /// tells the family.
    /// </summary>
    private static readonly (ExpertField Field, string[][] Groups)[] Fields =
    [
        (ExpertField.Medical,
        [
            ["処方"], ["服用"], ["飲み合わせ"], ["のみあわせ"], ["副作用"], ["インスリン"],
            // 薬 / サプリ only when something is being asked about them.
            ["薬", "いい"], ["薬", "よい"], ["薬", "大丈夫"], ["薬", "だいじょうぶ"],
            ["薬", "どう"], ["薬", "やめ"], ["薬", "止め"], ["薬", "量"], ["薬", "増や"],
            ["薬", "減ら"], ["薬", "一緒"], ["薬", "いっしょ"], ["薬", "効"], ["薬", "強"],
            ["くすり", "いい"], ["くすり", "大丈夫"], ["くすり", "どう"], ["くすり", "いっしょ"],
            ["サプリ", "いい"], ["サプリ", "大丈夫"], ["サプリ", "どう"]
        ]),
        (ExpertField.Care,
        [
            ["介護認定"], ["要介護"], ["要支援"], ["ケアマネ"], ["介護保険"], ["地域包括"]
        ]),
        (ExpertField.Money,
        [
            ["相続"], ["遺言"], ["後見人"], ["成年後見"]
        ])
    ];

    /// <summary>
    /// Words that name one of these subjects without themselves being a question.
    ///
    /// 「デイサービスに行ってきました」「税金の紙が届いた」「手術の跡はもう痛くない」 are a
    /// resident telling us about their day. Answering those with 「わたしからはお答えしません」
    /// is worse than saying nothing: it turns the one moment of contact they initiated into a
    /// rebuff, and it teaches them the assistant does not want to hear about their life.
    ///
    /// So these only refer when the message also *asks* something — see <see cref="AskMarkers"/>.
    /// The narrow wordings above (飲み合わせ, 要介護, 相続 …) stay unconditional because they
    /// are practically never small talk.
    /// </summary>
    private static readonly (ExpertField Field, string[] Words)[] Topics =
    [
        (ExpertField.Medical, ["手術", "認知症", "診断", "ワクチン", "予防接種", "点滴", "入院"]),
        (ExpertField.Care, ["デイサービス", "老人ホーム", "介護施設", "介護", "施設"]),
        (ExpertField.Money, ["年金", "確定申告", "保険金", "契約書", "弁護士", "税金", "保険"])
    ];

    /// <summary>
    /// Wording that turns a subject into a question. Deliberately excludes 「手続き」 and
    /// 「終わった」-style reports: 「保険金の手続きは終わったよ」 is news, not a request.
    /// </summary>
    private static readonly string[] AskMarkers =
    [
        "どう", "どこ", "どちら", "どれ", "いくら", "いつ", "だれ", "誰",
        "いい", "よい", "べき", "できる", "できます", "できません", "したい", "たい",
        "教え", "おしえ", "方法", "やり方", "申請", "受け", "もらえ", "なりますか",
        "ですか", "ますか", "でしょうか", "わから", "分から", "困", "心配", "相談",
        "必要", "大丈夫", "だいじょうぶ"
    ];

    /// <summary>
    /// Returns the referral for <paramref name="message"/>, or null when nothing in it
    /// belongs to a professional. Never throws and never calls out of process.
    /// </summary>
    public static ExpertReferral? TryRefer(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var normalized = Normalize(message);
        if (normalized.Length == 0)
        {
            return null;
        }

        foreach (var (field, groups) in Fields)
        {
            if (groups.Any(group => group.All(k => normalized.Contains(Normalize(k), StringComparison.Ordinal))))
            {
                return new ExpertReferral(field, ReplyFor(field));
            }
        }

        // Broad subjects refer only when the resident is asking rather than reporting.
        // The question mark is checked on the raw text because Normalize strips punctuation.
        var asking = message.Contains('?') || message.Contains('？')
            || AskMarkers.Any(m => normalized.Contains(Normalize(m), StringComparison.Ordinal));
        if (asking)
        {
            foreach (var (field, words) in Topics)
            {
                if (words.Any(w => normalized.Contains(Normalize(w), StringComparison.Ordinal)))
                {
                    return new ExpertReferral(field, ReplyFor(field));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The answer given when the language-model router classified a message as needing a
    /// professional but no keyword named which one.
    /// </summary>
    public static ExpertReferral General { get; } = new(ExpertField.Medical, GeneralReply);

    private const string GeneralReply =
        "大切なお話なので、わたしが決めてお伝えするのは控えますね。\n" +
        "かかりつけのお医者さんや、お住まいの地域の相談窓口にお尋ねください。\n" +
        "ご家族にお伝えしたいときは「家族に連絡」と送ってください。";

    private static string ReplyFor(ExpertField field) => field switch
    {
        ExpertField.Medical =>
            "ご心配ですね。お体やお薬のことは、まちがえると危ないので、わたしからはお答えしません。\n" +
            "かかりつけのお医者さんか、お薬をもらった薬局の薬剤師さんにお尋ねください。\n" +
            "ご家族に相談したいときは「家族に連絡」と送ってください。すぐにお伝えします。",

        ExpertField.Care =>
            "大事なお話ですね。介護のお手続きは地域によって違いますので、わたしからは決めずにおきますね。\n" +
            "お近くの地域包括支援センターか、担当のケアマネジャーさんにご相談ください。\n" +
            "ご家族にも伝えておきたいときは「家族に連絡」と送ってください。",

        ExpertField.Money =>
            "大切なお話なので、わたしからお答えするのは控えますね。まちがえると取り返しがつきません。\n" +
            "お金や書類のことは、ご家族と、年金事務所や市役所の窓口にご相談ください。\n" +
            "ご家族にお伝えしたいときは「家族に連絡」と送ってください。",

        _ => GeneralReply
    };

    /// <summary>Same folding as the knowledge base, so 「くすり」「ｸｽﾘ」「お 薬」 all compare equal.</summary>
    private static string Normalize(string value)
    {
        var folded = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(folded.Length);

        foreach (var ch in folded)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
