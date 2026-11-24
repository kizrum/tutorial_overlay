using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TutorialOverlay;

/// <summary>
/// What cannot be undone: buying, paying, ordering, deleting for good, sending in the user's name. A control whose
/// name says so is marked red ("only you press this") in every lesson, and the autoplay never presses it: it stops
/// and the user decides. Anything may add danger (the spec's danger:true, these words, the user's own words in the
/// settings); nothing takes it away — a needless stop costs one click of the user's, a missed one costs money.
/// </summary>
internal static class Danger
{
    internal sealed record Hit(string Kind, string Word);

    // whole words or phrases, matched on the normalised text (lower case, ё → е, punctuation as spaces)
    private static readonly (string kind, string[] words)[] Words =
    {
        ("payment", new[]
        {
            "купить", "купить сейчас", "оплатить", "оформить заказ", "подтвердить заказ", "подтвердить покупку",
            "подтвердить оплату", "заказать", "перевести деньги", "перевести средства", "подписаться", "продлить подписку", "приобрести",
            "buy", "buy now", "pay", "pay now", "checkout", "check out", "place order", "purchase", "subscribe",
            "confirm purchase", "confirm payment", "confirm order", "transfer money", "transfer funds", "complete purchase", "order now",
            "comprar", "comprar ahora", "pagar", "pagar ahora", "finalizar compra", "realizar pedido", "tramitar pedido",
            "confirmar pedido", "confirmar compra", "confirmar pago", "suscribirse", "suscribirme", "transferir dinero",
            "kaufen", "jetzt kaufen", "bezahlen", "jetzt bezahlen", "zur kasse", "bestellen", "jetzt bestellen",
            "zahlungspflichtig bestellen", "kauf bestätigen", "zahlung bestätigen", "bestellung bestätigen", "abonnieren",
            "geld überweisen",
            "购买", "立即购买", "支付", "立即支付", "付款", "结算", "去结算", "提交订单", "确认订单", "确认支付", "确认付款",
            "订阅", "转账",
        }),
        ("irreversible", new[]
        {
            "удалить навсегда", "удалить безвозвратно", "удалить аккаунт", "удалить учетную запись", "очистить корзину",
            "стереть все", "сбросить настройки", "сбросить до заводских", "форматировать диск",
            "delete permanently", "permanently delete", "delete forever", "delete account", "empty recycle bin",
            "empty trash", "erase all", "factory reset", "format disk",
            "eliminar permanentemente", "eliminar definitivamente", "borrar definitivamente", "eliminar cuenta",
            "vaciar papelera", "borrar todo", "restablecer de fábrica", "formatear disco",
            "endgültig löschen", "dauerhaft löschen", "konto löschen", "papierkorb leeren", "alles löschen",
            "auf werkseinstellungen zurücksetzen", "datenträger formatieren",
            "永久删除", "彻底删除", "删除账户", "删除帐户", "清空回收站", "全部清除", "恢复出厂设置", "格式化磁盘",
        }),
        ("send", new[]
        {
            "отправить", "опубликовать", "send", "publish", "post",
            "enviar", "publicar", "senden", "absenden", "veröffentlichen", "发送", "发布",
        }),
    };

    // a name that also says one of these is not the act itself (a cart, a reminder, a list)
    private static readonly string[] Not =
    {
        "в корзину", "добавить в корзину", "купить позже", "в избранное", "в список желаемого", "удалить фильтр",
        "удалить из списка", "отправить отзыв о приложении",
        "add to cart", "add to basket", "buy later", "wishlist", "wish list", "remove filter", "send feedback",
        "añadir al carrito", "agregar al carrito", "comprar más tarde", "lista de deseos", "eliminar filtro", "enviar comentarios",
        "in den warenkorb", "zum warenkorb hinzufügen", "später kaufen", "wunschliste", "merkliste", "filter entfernen", "feedback senden",
        "加入购物车", "添加到购物车", "稍后购买", "愿望单", "删除筛选", "发送反馈",
    };

    /// <summary>The user's own words (settings), added to the list.</summary>
    public static volatile string[] Extra = Array.Empty<string>();

    /// <summary>Names longer than this are a sentence, not a control's caption: not judged by their words.</summary>
    private const int MaxLength = 60;

    internal static string Norm(string s)
    {
        var b = new StringBuilder(s.Length + 2).Append(' ');
        foreach (var ch0 in s.ToLowerInvariant())
        {
            char ch = ch0 == 'ё' ? 'е' : ch0;
            b.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        b.Append(' ');
        var t = b.ToString();
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        return t.Length == 1 ? " " : t;
    }

    // Chinese and Japanese put no spaces between words: there the phrase is looked for anywhere in the name
    private static bool Has(string norm, string phrase) =>
        norm.Contains(Norm(phrase)) || (phrase.Any(c => c is >= '\u3040' and <= '\u9FFF') && norm.Contains(Norm(phrase).Trim()));

    /// <summary>Does this caption or name say it cannot be undone? null: it does not.</summary>
    public static Hit? Of(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxLength) return null;
        var n = Norm(name);
        if (n.Trim().Length == 0) return null;
        foreach (var w in Extra)
            if (!string.IsNullOrWhiteSpace(w) && Has(n, w)) return new Hit("user", w.Trim());
        if (Not.Any(x => Has(n, x))) return null;
        foreach (var (kind, words) in Words)
            foreach (var w in words)
                if (Has(n, w)) return new Hit(kind, w);
        return null;
    }

    /// <summary>Keys that destroy whatever is selected without asking: Shift+Delete.</summary>
    public static Hit? OfChord(Chord c) =>
        c.Vk == 0x2E && (c.Mods & Native.MOD_SHIFT) != 0 ? new Hit("irreversible", "Shift+Delete") : null;

    /// <summary>Keys that press the focused or default button (Enter, Space, Ctrl+Enter): it is judged by its name.</summary>
    public static bool PressesButton(Chord c) => c.Vk is 0x0D or 0x20;

    /// <summary>The first danger among a control's names (the spec's name, UI Automation's name, the found text).</summary>
    public static Hit? OfAny(IEnumerable<string?> names)
    {
        foreach (var n in names)
            if (Of(n) is Hit h) return h;
        return null;
    }
}
