using RT.VisualStudio;

namespace ReformatComments;

internal class Program
{
    static void Main(string[] args)
    {
        var code = File.ReadAllText(args[0]);
        code = CommentFormatter.ReformatComments(code);
        File.Copy(args[0], args[0] + ".bak", overwrite: true);
        File.WriteAllText(args[0], code);
    }
}