using RT.Util;
using RT.VisualStudio;

namespace ReformatComments;

internal class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine($"Usage: ReformatComments.exe <file.cs> [<backup-path>]");
            return -1;
        }
        var code = File.ReadAllText(args[0]);
        code = CommentFormatter.ReformatComments(code);
        var backupPath = args[0] + ".bak";
        if (args.Length >= 2)
        {
            Directory.CreateDirectory(args[1]);
            backupPath = Path.Combine(args[1], PathUtil.AppendBeforeExtension(Path.GetFileName(args[0]), $".{DateTime.Now:yyyy-MM-dd--HH.mm.ss.fff}"));
        }
        File.Copy(args[0], backupPath, overwrite: true);
        File.WriteAllText(args[0], code);
        return 0;
    }
}
