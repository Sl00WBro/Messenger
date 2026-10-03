using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;


var url = args.Length > 0 ? args[0] : "https://localhost:7045";
var n = args.Length > 1 ? int.Parse(args[1]) : 100;
var perClient = args.Length > 2 ? int.Parse(args[2]) : 5;
const double LimitMs = 10_000; 

var prefix = $"load{DateTime.UtcNow:HHmmss}_"; 
var joinMs = new ConcurrentBag<double>();
var sendMs = new ConcurrentBag<double>();
var deliveryMs = new ConcurrentBag<double>();
var historyMs = new ConcurrentBag<double>();
var errors = new ConcurrentBag<string>();
long received = 0, sent = 0;

var clients = Enumerable.Range(0, n).Select(i => new LoadClient($"{prefix}{i}")).ToList();

async Task Setup(LoadClient c)
{
    try
    {
        c.Conn = new HubConnectionBuilder().WithUrl(url).Build();

        c.Conn.On<UserDto[]>("UpdateUsers", users => c.Users = users);

        c.Conn.On<int, string, int, string>("ReceiveMessage", (_, _, _, text) =>
        {
            var parts = text.Split('|');
            if (parts.Length >= 2 && long.TryParse(parts[1], out var ticks))
            {
                var ms = (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerMillisecond;
                deliveryMs.Add(ms);
            }
            Interlocked.Increment(ref received);
        });

        await c.Conn.StartAsync();

        var sw = Stopwatch.StartNew();
        await c.Conn.InvokeAsync("JoinToMessage", c.Name);
        joinMs.Add(sw.Elapsed.TotalMilliseconds);
        c.Joined = true;
    }
    catch (Exception ex)
    {
        errors.Add($"[{c.Name}] подключение: {ex.Message}");
    }
}

var total = Stopwatch.StartNew();
await Task.WhenAll(clients.Select(Setup));

var alive = clients.Where(c => c.Joined).ToList();

if (alive.Count < 2)
{
    Console.WriteLine("тест остановлен");
    PrintErrors();
    return 1;
}

var deadline = DateTime.UtcNow.AddSeconds(30);
while (DateTime.UtcNow < deadline &&
       alive.Any(c => c.Users.Count(u => u.Username.StartsWith(prefix)) < alive.Count))
{
    await Task.Delay(200);
}

await Task.WhenAll(alive.Select(async c =>
{
    for (var i = 0; i < perClient; i++)
    {
        try
        {
            LoadClient target;
            do { target = alive[Random.Shared.Next(alive.Count)]; } while (target == c);

            var targetId = target.MyId;
            if (targetId == 0) { errors.Add($"[{c.Name}] не знает id {target.Name}"); continue; }

            var text = $"hi|{DateTime.UtcNow.Ticks}|{i}";
            var sw = Stopwatch.StartNew();
            await c.Conn!.InvokeAsync("SendPrivateMessage", targetId, text);
            sendMs.Add(sw.Elapsed.TotalMilliseconds);
            Interlocked.Increment(ref sent);
        }
        catch (Exception ex)
        {
            errors.Add($"[{c.Name}] отправка: {ex.Message}");
        }

        await Task.Delay(Random.Shared.Next(50, 300));
    }
}));

var waitUntil = DateTime.UtcNow.AddSeconds(15);
while (DateTime.UtcNow < waitUntil && Interlocked.Read(ref received) < Interlocked.Read(ref sent))
{
    await Task.Delay(100);
}

await Task.WhenAll(alive.Select(async (c, idx) =>
{
    try
    {
        var other = alive[(idx + 1) % alive.Count];
        var sw = Stopwatch.StartNew();
        await c.Conn!.InvokeAsync<List<JsonElement>>("GetHistory", other.MyId);
        historyMs.Add(sw.Elapsed.TotalMilliseconds);
    }
    catch (Exception ex)
    {
        errors.Add($"[{c.Name}] история: {ex.Message}");
    }
}));

total.Stop();

Console.WriteLine();
Console.WriteLine($"Клиентов подключено:   {alive.Count}/{n}");
Console.WriteLine($"Сообщений отправлено:  {Interlocked.Read(ref sent)}");
Console.WriteLine($"Сообщений доставлено:  {Interlocked.Read(ref received)}");
Console.WriteLine($"Потеряно:              {Interlocked.Read(ref sent) - Interlocked.Read(ref received)}");
Console.WriteLine($"Ошибок:                {errors.Count}");
Console.WriteLine($"Общее время теста:     {total.Elapsed.TotalSeconds:F1} с");
Console.WriteLine();
PrintStats("Вход", joinMs);
PrintStats("Отправка", sendMs);
PrintStats("Доставка получателю", deliveryMs);
PrintStats("Чтение истории)", historyMs);
PrintErrors();

var maxAll = new[] { joinMs, sendMs, deliveryMs, historyMs }
    .SelectMany(b => b).DefaultIfEmpty(0).Max();

var ok = alive.Count == n
         && errors.IsEmpty
         && Interlocked.Read(ref sent) == Interlocked.Read(ref received)
         && maxAll <= LimitMs;

Console.WriteLine();
Console.WriteLine(ok
    ? $"тест пройден: {n} клиентов, максимальная задержка {maxAll:F0} мс (лимит {LimitMs:F0} мс)"
    : "тест не пройден");

foreach (var c in clients)
{
    if (c.Conn is not null) await c.Conn.DisposeAsync();
}

return ok ? 0 : 1;

void PrintStats(string title, IEnumerable<double> values)
{
    var list = values.OrderBy(x => x).ToList();
    if (list.Count == 0) { Console.WriteLine($"{title,-34} нет данных"); return; }

    double P(double p) => list[(int)Math.Min(list.Count - 1, Math.Ceiling(p * list.Count) - 1)];
    Console.WriteLine(
        $"{title,-34} avg {list.Average(),7:F0} мс | p95 {P(0.95),7:F0} мс | max {list[^1],7:F0} мс");
}

void PrintErrors()
{
    if (errors.IsEmpty) return;
    Console.WriteLine("\nошибки:");
    foreach (var e in errors.Take(10)) Console.WriteLine("  " + e);
}

class LoadClient
{
    public LoadClient(string name) { Name = name; }
    public string Name { get; }
    public HubConnection? Conn { get; set; }
    public volatile UserDto[] Users = Array.Empty<UserDto>();
    public bool Joined { get; set; }
    public int MyId => Users.FirstOrDefault(u => u.Username == Name)?.Id ?? 0;
}

record UserDto(int Id, string Username);
