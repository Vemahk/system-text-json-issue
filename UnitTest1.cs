using System.Text;
using System.Text.Json;

namespace STJProblem;

[TestFixture]
public class WhenCausingProblems
{
    readonly struct TestB
    {
        public required int? Value { get; init; }
    }

    class TestA
    {
        public required int? A { get; init; }
        public required TestB? B { get; init; } // commenting this line makes it all work?
        public required string? C { get; init; }
        public required HashSet<string> D { get; init; }
    }

    [Test]
    public async Task ThenLargeReproducable()
    {
        var token = TestContext.CurrentContext.CancellationToken;
        var gen = new RandomGenerator(1905742104)
            .Register(r => new TestB { Value = r.NullOr(.5, r.Create<int>) })
            .Register(r => new TestA
            {
                A = r.NullOr(.5, r.Create<int>),
                B = r.NullOr(.5, r.Create<TestB>),
                C = r.NullOr(.5, r.Create<string>),
                D = r.CreateArray<string>(0, 5).ToHashSet(),
            });

        var array = gen.CreateArray<TestA>(8000);
        await TestFile(array, token); // fails on stream deserializeasync, $[7924], which is...
        /* `jq '.[7924]' <guid>`
{
  "a": 0,
  "b": {
    "value": null
  },
  "c": null,
  "d": []
}
         */ 
    }

    // so let's try to just use that failing value.
    [Test]
    public async Task ThenLargeNonRandom()
    {
        var token = TestContext.CurrentContext.CancellationToken;

        const int count = 1 << 14; // 16K
        var array = new TestA[count];
        Array.Fill(array, new TestA
        {
            A = 0,
            B = new TestB
            {
                Value = null,
            },
            C = null,
            D = [],
        });

        await TestFile(array, token); // passes.
    }


    [Test, Ignore("Used to find failing seed")]
    public async Task ThenLargeRandom()
    {
        var token = TestContext.CurrentContext.CancellationToken;
        var gen = new RandomGenerator()
            .Register(r => new TestB { Value = r.NullOr(.5, r.Create<int>) })
            .Register(r => new TestA
            {
                A = r.NullOr(.5, r.Create<int>),
                B = r.NullOr(.5, r.Create<TestB>),
                C = r.NullOr(.5, r.Create<string>),
                D = r.CreateArray<string>(0, 5).ToHashSet(),
            });

        var array = gen.CreateArray<TestA>(1 << 16);
        await TestFile(array, token);
    }

    private static async Task TestFile<T>(T data, CancellationToken token)
    {
        var dirpath = Path.Join(".", "TEMP");
        Directory.CreateDirectory(dirpath);
        var filepath = Path.Join(dirpath, Guid.NewGuid().ToString("N"));
        filepath = Path.GetFullPath(filepath);
        Console.WriteLine(filepath);
        await using var stream = File.Open(filepath, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete);

        var opts = new JsonSerializerOptions(JsonSerializerOptions.Default) { PropertyNameCaseInsensitive = true, };
        await JsonSerializer.SerializeAsync(stream, data, opts, token);
        
        stream.Seek(0, SeekOrigin.Begin);
        string json;
        using (var reader = new StreamReader(stream, Encoding.UTF8, true, -1, true))
        {
            json = await reader.ReadToEndAsync(token);
        }

        try
        {
            _ = JsonSerializer.Deserialize<T>(json, opts); // parsing by string works
            stream.Seek(0, SeekOrigin.Begin);

            stream.Seek(0, SeekOrigin.Begin);
            _ = await JsonSerializer.DeserializeAsync<T>(stream, opts, token); // fails occur here
            // _ = JsonSerializer.Deserialize<T>(stream, opts); // this also fails
        }
        catch (JsonException je)
        {
            await Console.Error.WriteLineAsync($"Error Path: {je.Path}");
            throw;
        }
    }

}

public class RandomGenerator
{
    private Random Rand { get; }
    private Dictionary<Type, Func<RandomGenerator, object>> Funcs { get; } = [];

    public RandomGenerator() : this(GenerateSeed()) { }

    public RandomGenerator(int seed)
    {
        Rand = new Random(seed);

        Register(r => r.Rand.Next()); // int32
        Register(r => NextString(r.Rand, r.Rand.Next(5, 10))); // string
    }

    public RandomGenerator Register<T>(Func<RandomGenerator, T> func) where T : notnull
    {
        Funcs.TryAdd(typeof(T), r => func(r));
        return this;
    }

    public T Create<T>() => (T)CreateBoxed(typeof(T));
    public T[] CreateArray<T>(int count) where T : notnull
    {
        var arr = new T[count];
        for (var i = 0; i < count; i++)
            arr[i] = Create<T>();
        return arr;
    }

    public T[] CreateArray<T>(int min, int max) where T : notnull
    {
        var count = Rand.Next(min, max + 1);
        return CreateArray<T>(count);
    }

    public T? NullOr<T>(double nullProb, Func<T> gen)
    {
        if (Rand.NextDouble() < nullProb) return default;
        return gen();
    }

    private object CreateBoxed(Type type)
    {
        if (!Funcs.TryGetValue(type, out var func))
            throw new ApplicationException($"Failed to generate type {type.FullName}: was not registered.");

        return func(this);
    }

    private static int GenerateSeed()
    {
        var seed = Random.Shared.Next();
        Console.WriteLine($"Using seed: {seed}");
        return seed;
    }

    public static string NextString(Random rand, int len)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < len; i++)
        {
            Rune rune;
            while (!Rune.TryCreate(rand.Next(0, 0x110000), out rune)) ;
            sb.Append(rune.ToString());
        }

        return sb.ToString();
    }
}