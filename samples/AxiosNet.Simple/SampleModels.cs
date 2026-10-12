namespace AxiosNet.Simple;

// 示例 DTO

public sealed record Address(string City, string Zip);

public sealed record User(long Id, string Name, int Age, bool Active, double Score, Address? Address, string[]? Roles);
