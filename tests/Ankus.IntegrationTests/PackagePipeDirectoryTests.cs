using System.IO.Pipes;
using System.Text;

namespace Ankus.IntegrationTests;

/// <summary>
/// Exercises real test-controller sockets with short, deep and multibyte temporary paths.
/// </summary>
/// <param name="context">The cooperative test cancellation context.</param>
[TestClass]
public sealed class PackagePipeDirectoryTests(TestContext context)
{
    /// <summary>
    /// Exchanges bytes through the selected directory while retaining the caller's build directory.
    /// </summary>
    /// <param name="directoryBytes">The exact UTF-8 directory length, or zero to use the owned root.</param>
    /// <param name="unicode">Whether the directory component contains multibyte characters.</param>
    /// <param name="reusesTemporary">Whether the complete monitoring socket fits in the temporary directory.</param>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [DataRow(0, false, true)]
    [DataRow(140, false, false)]
    [DataRow(56, false, true)]
    [DataRow(57, false, false)]
    [DataRow(70, false, false)]
    [DataRow(56, true, true)]
    [DataRow(57, true, false)]
    [DataRow(70, true, false)]
    public async Task SocketDirectorySupportsLongTemporaryPaths(int directoryBytes, bool unicode, bool reusesTemporary)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string root = IntegrationEnvironment.PhysicalDirectory(Directory.CreateDirectory(
            Path.Join(profile, ".apt-" + Guid.NewGuid().ToString("N")[..12])));
        string temporary = root;
        string? selected = null;
        try
        {
            if (directoryBytes != 0)
            {
                int componentBytes = directoryBytes - Encoding.UTF8.GetByteCount(root) - 1;
                Assert.IsGreaterThan(0, componentBytes, "The owned root must leave room for the boundary directory.");
                string component = unicode
                    ? new string('é', componentBytes / 2) + new string('a', componentBytes % 2)
                    : new string('a', componentBytes);
                temporary = Directory.CreateDirectory(Path.Join(root, component)).FullName;
                Assert.AreEqual(directoryBytes, Encoding.UTF8.GetByteCount(temporary));
            }

            selected = PackagePipeDirectory.Create(temporary, profile);
            Assert.AreEqual(reusesTemporary, selected == temporary);
            foreach (string prefix in new[] { string.Empty, "MONITORTOHOST_" })
            {
                string pipe = Path.Join(selected, prefix + Guid.NewGuid().ToString("N"));
                using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await Task.WhenAll(server.WaitForConnectionAsync(context.CancellationToken),
                    client.ConnectAsync(context.CancellationToken));
                await client.WriteAsync(new byte[] { 42 }, context.CancellationToken);
                byte[] received = new byte[1];
                await server.ReadExactlyAsync(received, context.CancellationToken);
                Assert.AreEqual((byte)42, received[0]);
            }

            Assert.IsTrue(Directory.Exists(temporary));
        }
        finally
        {
            if (selected is not null && selected != temporary)
            {
                Directory.Delete(selected, recursive: true);
            }

            Directory.Delete(root, recursive: true);
        }
    }
}
