namespace MarketplaceHub.Api.Security;

// The bundled data behind PasswordPolicy: words that the most common passwords are built from (English,
// Turkish, sports clubs, cities, this product and the marketplaces it talks to), whole phrases that are
// long enough on their own, and keyboard rows. All entries are lower case ASCII.
internal static class CommonPasswords
{
    public const int ShortestWord = 3;

    public static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        // English
        "password", "passwort", "passw", "pass", "passcode", "pwd", "secret", "secure", "security", "private", "login", "logon", "letmein",
        "welcome", "hello", "hallo", "admin", "administrator", "root", "user", "guest", "default", "master", "manager", "owner", "test", "tester",
        "testing", "demo", "temp", "temporary", "change", "changeme", "changeit", "access", "system", "server", "service", "support", "office",
        "company", "business", "account", "money", "dollar", "euro", "lira", "bank", "online", "internet", "computer", "windows", "microsoft",
        "google", "apple", "iphone", "android", "samsung", "facebook", "instagram", "twitter", "whatsapp", "youtube", "netflix", "amazon", "ebay",
        "dragon", "monkey", "tiger", "lion", "eagle", "falcon", "shadow", "ninja", "batman", "superman", "spiderman", "pokemon", "starwars",
        "master", "killer", "hunter", "soccer", "football", "baseball", "basketball", "hockey", "golf", "tennis", "player", "winner", "champion",
        "freedom", "liberty", "justice", "trustno", "trust", "nobody", "everybody", "whatever", "anything", "something", "nothing", "forever",
        "love", "iloveyou", "lovely", "lover", "loveme", "sweet", "sweetheart", "honey", "baby", "angel", "princess", "prince", "queen", "king",
        "sunshine", "summer", "winter", "spring", "autumn", "flower", "rainbow", "purple", "orange", "yellow", "green", "black", "white", "silver",
        "golden", "gold", "diamond", "crystal", "star", "moon", "sun", "sky", "ocean", "water", "fire", "earth", "mother", "father", "family",
        "friend", "friends", "happy", "lucky", "magic", "music", "guitar", "pepper", "cheese", "cookie", "chocolate", "coffee", "banana",
        "michael", "jessica", "jennifer", "thomas", "charlie", "robert", "daniel", "matthew", "andrew", "joshua", "jordan", "michelle", "ashley",
        "qwerty", "qwert", "qwertz", "azerty", "asdf", "asdfgh", "asdfghjkl", "zxcv", "zxcvbn", "zxcvbnm", "qazwsx", "qwertyuiop", "abc",
        "abcd", "abcdef", "xyz", "zaq", "qwe", "asd", "zxc", "one", "two", "three", "first", "second", "new", "old", "my", "the", "and", "you",
        "letmein", "open", "sesame", "opensesame", "monday", "friday", "sunday", "january", "december", "spring", "autumn",
        // Turkish
        "sifre", "parola", "gizli", "giris", "kullanici", "yonetici", "sistem", "deneme", "merhaba", "selam", "hosgeldin", "sevgi", "seviyorum",
        "askim", "ask", "canim", "bebegim", "hayat", "hayatim", "annem", "babam", "aile", "kardes", "arkadas", "dostum", "gunes", "yildiz", "deniz",
        "bahar", "yaz", "kis", "sonbahar", "mavi", "kirmizi", "sari", "yesil", "siyah", "beyaz", "aslan", "kartal", "kaplan", "kurt", "bozkurt",
        "turk", "turkiye", "turkey", "vatan", "millet", "bayrak", "ataturk", "mustafa", "kemal", "mehmet", "ahmet", "ali", "ayse", "fatma", "emre",
        "murat", "mustafa", "huseyin", "hasan", "ibrahim", "ismail", "osman", "yusuf", "omer", "elif", "zeynep", "merve", "esra", "burak", "can",
        "galatasaray", "fenerbahce", "besiktas", "trabzonspor", "bursaspor", "goztepe", "cimbom", "fener", "kartal", "karsiyaka", "sampiyon",
        "istanbul", "ankara", "izmir", "bursa", "antalya", "adana", "konya", "kocaeli", "trabzon", "eskisehir", "mersin", "kayseri", "gaziantep",
        "magaza", "satis", "siparis", "kargo", "fatura", "urun", "stok", "muhasebe", "depo", "pazar", "pazaryeri", "dukkan", "ticaret", "sirket",
        // This product, its operator and the marketplaces it works with
        "ravencia", "raven", "marketplace", "market", "hub", "marketplacehub", "panel", "entegrasyon", "integration", "operasyon", "operation",
        "trendyol", "hepsiburada", "hepsi", "burada", "shopify", "pazarama", "nonbir", "efatura", "efaturam", "earsiv", "amazon", "ciceksepeti",
        "gittigidiyor", "sahibinden", "getir", "yemeksepeti", "kolay", "hizli", "guvenli", "guvenlik"
    };

    public static readonly HashSet<string> Phrases = new(StringComparer.Ordinal)
    {
        "password123456!", "marketplacehub1!", "password1234567", "password12345678", "password123456789", "passwordpassword",
        "qwertyuiopasdfgh", "qwertyuiopasdfghjkl", "qwertyuiop123456", "1q2w3e4r5t6y7u8i", "1qaz2wsx3edc4rfv", "zaq12wsxcde34rfv",
        "iloveyouiloveyou", "iloveyou12345678", "letmeinletmein", "welcome123456789", "administrator123", "administrator1!",
        "changeme12345678", "123456789012345", "1234567890123456", "0987654321098765", "abcdefghijklmnop", "abc123abc123abc1",
        "trustno1trustno1", "starwarsstarwars", "superman12345678", "batman1234567890", "football12345678", "baseball12345678",
        "galatasaray1905", "fenerbahce1907", "besiktas1903", "trabzonspor1967", "turkiye1923", "ataturk1881",
        "sifre1234567890", "parola1234567890", "ravencia123456!", "trendyol123456!", "hepsiburada1234!"
    };

    public static readonly string[] KeyboardRows =
    [
        "1234567890", "0987654321", "qwertyuiop", "poiuytrewq", "asdfghjkl", "lkjhgfdsa", "zxcvbnm", "mnbvcxz",
        "qwertzuiop", "azertyuiop", "1q2w3e4r5t6y7u8i9o0p", "1qaz2wsx3edc4rfv5tgb6yhn7ujm", "qazwsxedcrfvtgbyhnujm", "qweasdzxc"
    ];
}
