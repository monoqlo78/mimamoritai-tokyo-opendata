namespace MimamoriTai.Core.Domain;

/// <summary>A 東京都 municipality and a representative coordinate (its city or ward office).</summary>
/// <param name="Group">Heading it appears under in the picker: 区部 / 多摩 / 島しょ.</param>
public sealed record TokyoMunicipality(string Name, string Group, double Latitude, double Longitude);

/// <summary>
/// The 62 municipalities of 東京都, each with the coordinate of its city or ward office.
///
/// <para>
/// Families think in wards and cities; 気象庁 thinks in observation stations, and there are
/// only a handful in Tokyo that measure temperature at all -- none of them named after a
/// ward. Asking someone to pick "江戸川臨海" when they live in 葛飾区 is asking them to know
/// the observation network. So the picker offers the place they would say out loud, and this
/// table is what turns it into the nearest station that actually reports a temperature.
/// </para>
/// <para>
/// The coordinates are the municipal offices rather than centroids: they sit where people
/// actually live, which for a long municipality like 奥多摩町 or 大田区 is the more useful
/// end of it.
/// </para>
/// </summary>
public static class TokyoMunicipalities
{
    public const string Wards = "東京23区";
    public const string Tama = "多摩地域";
    public const string Islands = "島しょ部";

    public static IReadOnlyList<TokyoMunicipality> All { get; } =
    [
        new("千代田区", Wards, 35.6940, 139.7536),
        new("中央区", Wards, 35.6706, 139.7720),
        new("港区", Wards, 35.6581, 139.7514),
        new("新宿区", Wards, 35.6938, 139.7036),
        new("文京区", Wards, 35.7081, 139.7522),
        new("台東区", Wards, 35.7126, 139.7800),
        new("墨田区", Wards, 35.7107, 139.8015),
        new("江東区", Wards, 35.6731, 139.8172),
        new("品川区", Wards, 35.6092, 139.7302),
        new("目黒区", Wards, 35.6415, 139.6982),
        new("大田区", Wards, 35.5614, 139.7161),
        new("世田谷区", Wards, 35.6465, 139.6533),
        new("渋谷区", Wards, 35.6640, 139.6982),
        new("中野区", Wards, 35.7074, 139.6638),
        new("杉並区", Wards, 35.6994, 139.6365),
        new("豊島区", Wards, 35.7261, 139.7169),
        new("北区", Wards, 35.7528, 139.7336),
        new("荒川区", Wards, 35.7361, 139.7833),
        new("板橋区", Wards, 35.7512, 139.7093),
        new("練馬区", Wards, 35.7357, 139.6517),
        new("足立区", Wards, 35.7750, 139.8046),
        new("葛飾区", Wards, 35.7434, 139.8472),
        new("江戸川区", Wards, 35.7067, 139.8683),

        new("八王子市", Tama, 35.6664, 139.3160),
        new("立川市", Tama, 35.7139, 139.4079),
        new("武蔵野市", Tama, 35.7178, 139.5661),
        new("三鷹市", Tama, 35.6835, 139.5595),
        new("青梅市", Tama, 35.7879, 139.2758),
        new("府中市", Tama, 35.6689, 139.4776),
        new("昭島市", Tama, 35.7056, 139.3539),
        new("調布市", Tama, 35.6506, 139.5409),
        new("町田市", Tama, 35.5462, 139.4386),
        new("小金井市", Tama, 35.6994, 139.5030),
        new("小平市", Tama, 35.7285, 139.4774),
        new("日野市", Tama, 35.6714, 139.3950),
        new("東村山市", Tama, 35.7546, 139.4685),
        new("国分寺市", Tama, 35.7104, 139.4623),
        new("国立市", Tama, 35.6839, 139.4415),
        new("福生市", Tama, 35.7386, 139.3268),
        new("狛江市", Tama, 35.6347, 139.5786),
        new("東大和市", Tama, 35.7454, 139.4266),
        new("清瀬市", Tama, 35.7856, 139.5262),
        new("東久留米市", Tama, 35.7581, 139.5296),
        new("武蔵村山市", Tama, 35.7546, 139.3874),
        new("多摩市", Tama, 35.6369, 139.4463),
        new("稲城市", Tama, 35.6378, 139.5046),
        new("羽村市", Tama, 35.7673, 139.3110),
        new("あきる野市", Tama, 35.7288, 139.2939),
        new("西東京市", Tama, 35.7256, 139.5386),
        new("瑞穂町", Tama, 35.7730, 139.3557),
        new("日の出町", Tama, 35.7420, 139.2601),
        new("檜原村", Tama, 35.7261, 139.1490),
        new("奥多摩町", Tama, 35.8090, 139.0968),

        new("大島町", Islands, 34.7500, 139.3568),
        new("利島村", Islands, 34.5233, 139.2789),
        new("新島村", Islands, 34.3676, 139.2664),
        new("神津島村", Islands, 34.2059, 139.1381),
        new("三宅村", Islands, 34.0938, 139.4859),
        new("御蔵島村", Islands, 33.8963, 139.6033),
        new("八丈町", Islands, 33.1128, 139.7857),
        new("青ヶ島村", Islands, 32.4566, 139.7686),
        new("小笠原村", Islands, 27.0940, 142.1917),
    ];

    public static TokyoMunicipality? Find(string? name) =>
        name is { Length: > 0 } ? All.FirstOrDefault(m => m.Name == name) : null;
}
