using LocalDm.Infrastructure.Probe;

if (args.Length > 0 && args[0] == "--speech")
{
    return await SpeechProbe.RunAsync(args.Skip(1).ToArray());
}

if (args.Length > 0 && args[0] == "--import")
{
    return await ImportProbe.RunAsync(args.Skip(1).ToArray());
}

if (args.Length > 0 && args[0] == "--build-package")
{
    return await BuildPackageProbe.RunAsync(args.Skip(1).ToArray());
}

if (args.Length > 0 && args[0] == "--dm-acceptance")
{
    return await DmAcceptanceProbe.RunAsync(args.Skip(1).ToArray());
}

if (args.Length > 0 && args[0] == "--import-wizard")
{
    return await ImportWizardProbe.RunAsync(args.Skip(1).ToArray());
}

Console.WriteLine("LocalDm.Probe:");
Console.WriteLine("  --speech [--out <dir>] [--model <id>] [--voice <name>]");
Console.WriteLine("  --import --story <dir> [--pages N] [--tier Flash|Basic|Standard|Advanced] [--out <dir>]");
Console.WriteLine("  --build-package --story <剧本根目录> [--title T] [--players 5] [--out <dir>] [--data <dir>]");
Console.WriteLine("  --dm-acceptance [--data <dir>] [--settings <file>] [--out <dir>] [--phase story|chat] [--model <id>]");
return 1;
