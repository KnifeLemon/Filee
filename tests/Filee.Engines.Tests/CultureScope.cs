// A regional format for one test: what follows the PC's (Excel's built-in short date, number format 14) comes out the
// same on every machine. The culture flows with the test's async code and is set back when the scope ends.

using System.Globalization;

namespace Filee.Engines.Tests;

internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _before = CultureInfo.CurrentCulture;

    public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

    public void Dispose() => CultureInfo.CurrentCulture = _before;
}
