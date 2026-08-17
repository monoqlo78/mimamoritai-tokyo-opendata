using System.Globalization;
using System.Text;

namespace MimamoriTai.Core.Application;

/// <summary>How confident a knowledge-base lookup has to be before it answers.</summary>
public enum FaqMatchMode
{
    /// <summary>
    /// Used before the intent model runs. Only phrasings that cannot plausibly be a
    /// device command or a data question are allowed to answer, so the knowledge base
    /// can never steal "エアコンつけて" or "今日の様子は".
    /// </summary>
    Strict = 0,

    /// <summary>
    /// Used after the intent model has already classified the message as small talk.
    /// Single keywords are enough here, because nothing else wants the message.
    /// </summary>
    Loose = 1
}

/// <summary>A canned answer produced without calling any model.</summary>
public sealed record FaqAnswer(string Id, string Reply);

/// <summary>
/// The product knowledge an elderly resident actually asks about, held as data rather
/// than as prompt text.
///
/// Two reasons it is not simply appended to a system prompt:
///
/// 1. Latency. The LINE webhook cancels an event after 8 seconds and intent parsing
///    already spends ~1.7s of that. A lookup here costs no round trip at all, so the
///    most common questions are answered long before the budget matters.
/// 2. Truthfulness. Every answer describes a screen or a behaviour that exists in this
///    repository ("SwitchBot設定" → "LINEでお知らせを受け取る" → "連携コードを発行する").
///    A model asked to explain the product from scratch invents plausible menus, and a
///    confidently wrong instruction is worse for an 85 year old than no answer.
///
/// The same entries are also rendered into <see cref="ProductFacts"/> and handed to the
/// small-talk prompt, so a question phrased in a way no rule anticipated is still
/// answered from these facts instead of from the model's imagination.
/// </summary>
public static class AssistantKnowledgeBase
{
    /// <summary>
    /// Words that mean "something is happening to my body right now". These are checked
    /// before every other rule: a person typing 胸が痛い must never receive small talk.
    /// </summary>
    private static readonly string[] UrgentKeywords =
    [
        "たすけて", "助けて", "たおれ", "倒れ", "動けない", "うごけない", "救急", "きゅうきゅう",
        "119", "息苦し", "いきぐるし", "呼吸が", "血が出", "出血", "意識が", "けいれん", "痙攣",
        "胸が痛", "むねが痛", "胸がくるし", "胸が苦し", "しびれ", "痺れ", "骨折", "転んで", "ころんで"
    ];

    /// <summary>
    /// Sent when <see cref="IsUrgent"/> fires. Names the emergency number first, then the
    /// existing one-touch route, so the reply works whether or not the rich menu is visible.
    /// </summary>
    public const string UrgentReply =
        "つらいのですね。がまんしないでください。\n" +
        "強い痛み・息苦しさ・出血があるときは、すぐに119番へお電話ください。\n" +
        "画面下の「助けて」ボタンを押していただくと、ご家族にもすぐお知らせします。";

    /// <summary>Shown for 使い方 / ヘルプ, and pointed at by the webhook's timeout message.</summary>
    public const string HelpReply =
        "見守り隊です。次のことをお手伝いできます。\n" +
        "・「今日の様子」…暮らしの記録をお伝えします\n" +
        "・「家族に連絡」…ご家族に連絡してほしいと伝えます\n" +
        "・「体調が悪い」…ご家族にすぐお知らせします\n" +
        "「家族の追加方法は」「通知が来ない」のように、聞きたいことをそのまま送っていただいても大丈夫です。";

    /// <param name="PreIntent">
    /// Whether this entry may answer before the intent model has run. True only for
    /// wording that cannot also belong to a device command or a question about the
    /// resident's day: "家族の追加方法は" is always product help, but "痛いって言ってた?"
    /// could be a family member asking about the records, so that entry waits until the
    /// model has already called the message small talk.
    /// </param>
    /// <param name="Excludes">
    /// Words that veto a match outright.
    ///
    /// Some keywords are the right trigger for this product and an ordinary Japanese word
    /// everywhere else. 「費用」 means the app's own price here but the cost of a care home
    /// in 「施設の費用の相場はいくら」, and 「使い方」 means product help here but electricity
    /// consumption in 「電気の使い方が増えている」. Both were measured answering the wrong
    /// question in docs/eval/intent-accuracy.md. Rather than delete the keyword — which
    /// would break 「料金はかかりますか」 and 「使い方」, the word the app itself tells people
    /// to send — the entry names the company it must not keep.
    /// </param>
    private sealed record FaqEntry(
        string Id, string Reply, string[][] StrictGroups, string[] LooseKeywords, bool PreIntent,
        string[]? Excludes = null);

    /// <summary>
    /// Ordered; the first entry that matches answers. Specific worries are listed before
    /// general ones so 「カメラで撮られてるの?」 gets the camera answer, not the generic
    /// privacy one.
    /// </summary>
    private static readonly FaqEntry[] Entries =
    [
        // Before add-family: that entry claims every message containing 「連携コード」, and a
        // person whose code stopped working needs to be told it expired, not told again how
        // to issue one.
        new(
            "link-code-expired",
            "連携のコードは、発行してから10分で使えなくなります。1回使うと、それも使えなくなります。\n" +
            "もう一度「家族の追加」の「連携コードを発行する」を押して、新しい6けたの数字を出してください。\n" +
            "出たらすぐに「連携 123456」のように送ってください。",
            [
                ["連携コード", "使えな"], ["連携コード", "つかえな"], ["連携コード", "期限"],
                ["連携コード", "切れ"], ["連携コード", "間違"], ["連携コード", "エラー"],
                ["連携コード", "できな"], ["コード", "期限切れ"], ["コード", "無効"],
                ["連携", "できない"], ["連携", "できません"], ["連携", "失敗"]
            ],
            [],
            PreIntent: true),

        new(
            "add-family",
            "ご家族を追加するには、まず設定の画面を開きます。\n" +
            "1. 見守り隊の画面のいちばん上から「家族の追加」を押す\n" +
            "2. 「連携コードを発行する」を押す\n" +
            "3. 出てきた6けたの数字を、このトークに「連携 123456」のように送る\n" +
            "コードは10分で使えなくなります。過ぎたら、もう一度発行してください。",
            [
                ["家族", "追加"], ["家族", "登録"], ["家族", "増や"], ["家族", "ふや"],
                ["家族", "連携"], ["家族", "招待"], ["連携", "方法"], ["連携", "やり方"],
                ["連携", "したい"], ["連携", "どう"], ["連携コード"],
                ["息子", "追加"], ["娘", "追加"], ["孫", "追加"],
                // Elderly users often type entirely in hiragana.
                ["かぞく", "追加"], ["かぞく", "ついか"], ["家族", "ついか"],
                ["かぞく", "ふや"], ["ついか", "ほうほう"], ["れんけい", "ほうほう"]
            ],
            ["家族の追加", "連携のしかた", "連携の仕方"],
            PreIntent: true),

        new(
            "notification-missing",
            "お知らせが届かないときは、この3つをお確かめください。\n" +
            "1. このトークをブロックしていないか確かめる\n" +
            "2. LINEアプリの通知を「オン」にする\n" +
            "3. 見守り隊の画面のいちばん上の「家族の追加」で、ご家族との連携が終わっているか確かめる\n" +
            "それでも届かないときは、この画面をご家族に見せてください。",
            [
                ["通知", "来ない"], ["通知", "こない"], ["通知", "届か"], ["通知", "とどか"],
                ["お知らせ", "来ない"], ["お知らせ", "こない"], ["お知らせ", "届か"],
                ["連絡", "来ない"], ["連絡", "こない"], ["メッセージ", "来ない"],
                ["通知", "されない"], ["通知", "無い"], ["通知", "ない"]
            ],
            [],
            PreIntent: true),

        new(
            "sound-missing",
            "音が鳴らないときは、LINEの通知の音が切れていることがほとんどです。\n" +
            "1. スマホのマナーモードを解除する\n" +
            "2. LINEの「設定」→「通知」を「オン」にする\n" +
            "3. 音量のボタンで、音を大きくする",
            [
                ["音", "鳴らない"], ["音", "ならない"], ["音", "しない"], ["音", "出ない"],
                ["音", "でない"], ["音", "小さ"], ["着信音"], ["音", "聞こえ"]
            ],
            [],
            PreIntent: true),

        new(
            "font-size",
            "文字の大きさは、LINEアプリの設定で変えられます。\n" +
            "1. LINEの「ホーム」から歯車のマークを押す\n" +
            "2. 「トーク」を押す\n" +
            "3. 「フォントサイズ」で大きいものを選ぶ\n" +
            "見えにくいときは、遠慮なくご家族にお願いしてくださいね。",
            [
                ["文字", "大き"], ["字", "大き"], ["文字", "小さ"], ["字", "小さ"],
                ["文字", "見え"], ["文字", "読め"], ["文字", "読み"], ["フォント"]
            ],
            [],
            PreIntent: true),

        new(
            "add-device",
            "機器の追加は、ご家族の操作が必要です。\n" +
            "見守り隊の「SwitchBot設定」でSwitchBotとつなぐと、お部屋の機器が自動で読み込まれます。\n" +
            "むずかしいときは、無理をせずご家族にお願いしてください。",
            [
                ["機器", "追加"], ["機械", "追加"], ["端末", "追加"], ["センサー", "追加"],
                ["機器", "増や"], ["機器", "ふや"], ["機器", "登録"], ["センサー", "登録"],
                ["スイッチボット", "追加"], ["switchbot", "追加"], ["機器", "つなぎ"], ["機器", "接続"]
            ],
            [],
            PreIntent: true),

        new(
            "camera",
            "カメラはありません。映像も音声も、いっさい記録していません。\n" +
            "お伝えしているのは、ドアの開け閉めや電気の使われ方といった、機器の記録だけです。\n" +
            "お部屋の様子が見られることはありませんので、ご安心ください。",
            [
                ["カメラ"], ["かめら"], ["撮ら"], ["撮影"], ["写ら"],
                ["盗撮"], ["録画"], ["録音"], ["マイク"], ["聞かれ"], ["盗聴"],
                // 「写真」「映像」だけでは心配を表しません。「孫の写真が届いた」に
                // 「カメラはありません」と返すと、こちらから盗撮の話を持ち出す形になります。
                // 残る・見られるを気にする言い回しと組んだときだけ答えます。
                ["写真", "撮"], ["写真", "残"], ["写真", "保存"], ["写真", "見られ"],
                ["映像", "残"], ["映像", "保存"], ["映像", "見られ"], ["映像", "記録"]
            ],
            ["見られて", "みられて"],
            PreIntent: true),

        new(
            "surveillance",
            "見張るためのものではありません。\n" +
            "お元気に過ごされていることが分かると、ご家族が安心できる。それだけのための仕組みです。\n" +
            "映像はなく、ドアの開け閉めなどの記録だけをお伝えしています。おいやなときは、遠慮なくご家族にお伝えください。",
            [
                ["見張"], ["監視"], ["みはら"], ["管理されて"], ["のぞかれ"], ["覗かれ"],
                ["気持ち悪"], ["きもちわる"], ["嫌だ", "見守"], ["いやだ", "見守"]
            ],
            [],
            PreIntent: true),

        new(
            "privacy",
            "お預かりしているのは、ドアの開け閉めや電気の使われ方などの記録だけです。\n" +
            "映像や音声、通帳やお金の情報はいっさい扱いません。\n" +
            "記録を見られるのは、連携したご家族だけです。",
            [
                ["個人情報"], ["プライバシー"], ["情報", "大丈夫"], ["情報", "漏れ"],
                ["情報", "もれ"], ["データ", "大丈夫"], ["安全", "情報"], ["悪用"]
            ],
            [],
            PreIntent: true),

        new(
            "wrong-button",
            "大丈夫ですよ。まちがえて押しても、こわれたり、お金がかかったりすることはありません。\n" +
            "気になるときは「大丈夫」と送ってください。お元気だと、ご家族にお伝えします。",
            [
                ["間違え", "押"], ["まちがえ", "押"], ["押し間違"], ["おしまちが"],
                ["誤って", "押"], ["間違って", "押"], ["まちがって", "押"], ["間違え", "送"]
            ],
            [],
            PreIntent: true),

        new(
            "cost",
            "このLINEのやりとりに、お金はかかりません。\n" +
            "ボタンを押しても、お返事をしても、料金が増えることはありません。\n" +
            "見守り隊が通帳やお金の情報を見ることも、いっさいありません。",
            [
                ["料金"], ["お金", "かか"], ["お金", "いる"], ["お金", "とられ"],
                ["有料"], ["課金"], ["請求"], ["いくら", "かかり"], ["費用"], ["支払"]
            ],
            [],
            PreIntent: true,
            // Money questions about the resident's own life -- care fees, hospital bills,
            // the electricity bill -- are not questions about what this LINE costs.
            Excludes:
            [
                "介護", "施設", "老人ホーム", "ホーム", "入院", "病院", "医療", "手術",
                "年金", "相続", "保険", "税金", "電気", "水道", "ガス", "家賃"
            ]),

        new(
            "who-sees",
            "記録をご覧になれるのは、連携が済んでいるご家族だけです。\n" +
            "ご近所の方や、知らない人に見られることはありません。\n" +
            "どなたが連携しているかは、「SwitchBot設定」の画面でお確かめいただけます。",
            [
                ["誰", "見て"], ["誰", "見られ"], ["誰", "見える"], ["だれ", "見て"],
                ["だれ", "見られ"], ["誰が", "知って"], ["他人", "見"], ["近所", "見"]
            ],
            [],
            PreIntent: true),

        new(
            "stop-service",
            "いつでもおやめになれます。しばりはありません。\n" +
            "「SwitchBot設定」の画面で「接続を解除する」を押すと、記録は止まります。\n" +
            "ご自身でむずかしいときは、ご家族にそうお伝えください。それで大丈夫です。",
            [
                ["やめ", "たい"], ["やめる", "方法"], ["解約"], ["退会"], ["止め", "たい"],
                ["やめ", "られ"], ["解除", "したい"], ["使うのを", "やめ"], ["外し", "たい"]
            ],
            [],
            PreIntent: true),

        new(
            "device-not-responding",
            "機器の記録が届いていないのかもしれません。ご本人のせいではありませんので、ご安心ください。\n" +
            "ご家族に「SwitchBot設定」の「今すぐ同期する」を押してもらうと、直ることがあります。\n" +
            "そのままでも、暮らしに困ることはありません。",
            [
                ["機器", "反応"], ["センサー", "反応"], ["機器", "動かな"], ["センサー", "動かな"],
                ["機器", "記録", "ない"], ["反応", "しない", "センサー"], ["機器", "つながらな"]
            ],
            [],
            PreIntent: true),

        new(
            "burden",
            "ご迷惑なんてことは、ありませんよ。\n" +
            "お元気だと分かるだけで、ご家族はほっとされます。\n" +
            "変わりがなければ、お知らせもほとんど届きません。",
            [
                ["迷惑"], ["めいわく"], ["手間", "かけ"], ["負担", "かけ"],
                ["申し訳"], ["すまない", "家族"]
            ],
            [],
            PreIntent: false),

        new(
            "contact-family",
            "ご家族にお伝えできます。\n" +
            "「家族に連絡」と送っていただくか、画面下の「家族に連絡」ボタンを押してください。\n" +
            "すぐにお知らせします。",
            [
                ["家族", "話し"], ["家族", "はなし"], ["家族", "声"], ["電話", "したい"],
                ["電話", "かけ"], ["家族", "会い"], ["息子", "話"], ["娘", "話"],
                ["家族", "連絡", "したい"], ["寂しい", "家族"]
            ],
            [],
            PreIntent: false),

        new(
            "lonely",
            "そう感じる日も、ありますよね。お話ししてくださって、ありがとうございます。\n" +
            "ご家族とお話ししたいときは「家族に連絡」と送ってください。すぐにお伝えします。",
            [
                ["さみしい"], ["寂しい"], ["さびしい"], ["ひとりぼっち"], ["一人ぼっち"],
                ["心細"], ["こころぼそ"], ["つまらない"], ["泣き"]
            ],
            [],
            PreIntent: false),

        new(
            "unwell",
            "おつらいですね。無理をなさらないでください。\n" +
            "「体調が悪い」と送っていただくと、ご家族にすぐお知らせします。\n" +
            "強い痛みや息苦しさがあるときは、ためらわず119番へお電話ください。",
            [
                ["具合", "悪"], ["ぐあい", "悪"], ["体調", "悪"], ["たいちょう", "悪"],
                ["気分", "悪"], ["しんどい"], ["だるい"], ["熱が"], ["風邪"], ["めまい"],
                ["吐き気"], ["痛い"], ["いたい"], ["調子", "悪"]
            ],
            [],
            PreIntent: false),

        new(
            "what-is-this",
            HelpReply,
            [
                ["使い方"], ["つかいかた"], ["ヘルプ"], ["何ができ"], ["なにができ"],
                ["何をしてくれ"], ["なにをしてくれ"], ["どう使"], ["どうやって使"],
                ["このline", "何"], ["このline", "なに"], ["わからない", "使"]
            ],
            ["使い方", "ヘルプ", "説明", "メニュー"],
            PreIntent: true,
            // 「電気の使い方」「お金の使い方」「時間の使い方」 are about how the resident lives,
            // not about how this app works.
            Excludes: ["電気", "お金", "水", "ガス", "時間", "体", "からだ"])
    ];

    /// <summary>
    /// The same knowledge rendered for the small-talk prompt, so a question no rule
    /// anticipated is still grounded in what the product actually does.
    /// </summary>
    public static string ProductFacts { get; } = """
        【見守り隊について、事実として正しいこと】
        - 見守り隊は、離れて暮らすご高齢の家族が元気に過ごしているかを、ご家族が確認できるサービスです。
        - お伝えするのは「いつもどおり過ごされているか」です。朝起きて動き出したか、日中の様子に
          変わりがないか、といった暮らしの様子をご家族にお知らせします。
        - カメラもマイクもありません。映像・音声・写真はいっさい記録しません。
        - その様子は SwitchBot の機器のできごと（ドアの開け閉め、人の動き、電気の使われ方など）から
          読み取っています。機器の記録はあくまで手段で、お伝えしたいのは暮らしの様子のほうです。
        - ご家族(LINE)の追加手順: 画面いちばん上の「家族の追加」→「連携コードを発行する」
          → 出た6けたの数字を、LINEのトークに「連携 123456」のように送る。コードは10分間・1回だけ有効。
          （「家族の追加」を押すと専用画面が開き、そこに「ご家族の追加」欄があります）
        - 機器の追加は「SwitchBot設定」画面で SwitchBot と接続すると自動で読み込まれます。
        - 「SwitchBot設定」画面には「今すぐ同期する」と「接続を解除する」があります。解除すると記録は止まります。
        - LINEで使えるボタン:「助けて」「体調が悪い」「大丈夫」「今日の様子」「家族に連絡」。
        - 文字の大きさや通知音は、見守り隊ではなく LINE アプリ側の設定で変えます。
        - 見守り隊にお金の情報や通帳の情報はありません。ボタンを押しても料金はかかりません。
        - 記録を見られるのは、連携が済んだご家族だけです。

        【言ってはいけないこと】
        - 「ドアの開け閉めや電気の使い方をお知らせするサービス」という言い方をしないこと。
          それは手段であって目的ではありません。何ができるかを聞かれたら、まず
          「離れて暮らすご家族が元気に過ごされているかを確認できます」と答えること。
        - この一覧に無い画面名・ボタン名・手順を作って案内しないこと。
        - 健康・症状・薬・医療・介護認定・お金・年金・相続・法律について、自分で判断して答えないこと。
          お医者さん・薬剤師さん・地域包括支援センター・ご家族に相談するよう案内すること。
        - 分からないことは、分からないと正直に伝え、「使い方」と送るよう案内すること。
        """;

    /// <summary>
    /// True when the message describes a possible medical emergency. Checked before any
    /// other routing so these never fall through to small talk.
    /// </summary>
    public static bool IsUrgent(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = Normalize(message);
        return UrgentKeywords.Any(k => normalized.Contains(Normalize(k), StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns a canned answer for <paramref name="message"/>, or null when nothing is
    /// confidently known. Never throws and never calls out of process.
    /// </summary>
    public static FaqAnswer? TryAnswer(string? message, FaqMatchMode mode = FaqMatchMode.Strict)
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

        foreach (var entry in Entries)
        {
            if (mode == FaqMatchMode.Strict && !entry.PreIntent)
            {
                continue;
            }

            if (entry.Excludes is { Length: > 0 } excludes
                && excludes.Any(k => normalized.Contains(Normalize(k), StringComparison.Ordinal)))
            {
                continue;
            }

            if (entry.StrictGroups.Any(group => group.All(k => normalized.Contains(Normalize(k), StringComparison.Ordinal))))
            {
                return new FaqAnswer(entry.Id, entry.Reply);
            }

            if (mode == FaqMatchMode.Loose
                && entry.LooseKeywords.Any(k => normalized.Contains(Normalize(k), StringComparison.Ordinal)))
            {
                return new FaqAnswer(entry.Id, entry.Reply);
            }
        }

        return null;
    }

    /// <summary>
    /// Folds width, case and spacing so that "カメラ" / "ｶﾒﾗ" / "か め ら" all compare equal.
    /// Punctuation is dropped because elderly users type 「？」「。」and trailing spaces freely.
    /// </summary>
    private static string Normalize(string value)
    {
        var folded = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

        // Kanji/kana spellings of the same everyday word. Without this, 「何ができるの」
        // matched and 「何が出来るの」 did not, so two people asking the identical question
        // got answers from two different layers -- one canned, one from the model.
        foreach (var (written, kana) in SpellingVariants)
        {
            folded = folded.Replace(written, kana, StringComparison.Ordinal);
        }

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

    /// <summary>
    /// Applied to both the message and the keywords, so entries stay written in whichever
    /// spelling reads best while still matching the other one.
    /// </summary>
    private static readonly (string Written, string Kana)[] SpellingVariants =
    [
        ("出来", "でき"),
        ("分から", "わから"),
        ("判ら", "わから"),
        ("解ら", "わから"),
        ("仕方", "しかた"),
        ("使いかた", "使い方"),
        ("行なえ", "行え"),
    ];
}
