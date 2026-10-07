using System.Net;

namespace LampaWin.Core;

public sealed record FoodService(string Id, string Title, Uri HomeUri);

public static class FoodServices
{
    public static IReadOnlyList<FoodService> All { get; } = Array.AsReadOnly(new[]
    {
        new FoodService("burgerking", "Burger King", new Uri("https://burgerkingrus.ru/")),
        new FoodService("yandex", "Яндекс Go · Еда", new Uri("https://eda.yandex.ru/"))
    });

    public static string ProfileDirectory(string root, FoodService service)
    {
        if (!All.Contains(service)) throw new ArgumentException("Неизвестный сервис.", nameof(service));
        return Path.Combine(Path.GetFullPath(root), service.Id);
    }

    public static bool IsSafeNavigation(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && !uri.IsLoopback && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        && (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || PublicNetworkPolicy.IsPublic(address));
}
