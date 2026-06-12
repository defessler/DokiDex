using App;

if (args.Length == 0)
{
    Console.WriteLine("usage: app [--slug] <text>");
    return 1;
}

if (args[0] == "--slug")
{
    Console.WriteLine(StringUtils.Slugify(string.Join(" ", args[1..])));
    return 0;
}

Console.WriteLine(string.Join(" ", args));
return 0;
