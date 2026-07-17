using System.Buffers.Binary;

const int DefaultPeriod = 512;
const string DefaultSuffix = "_decrypted";
const string EmbeddedKeyBase64 =
    "fJXmQ4Z/dW8zNQxm7Qy1oIoxsHhSKVFb2Ci4MN/dvSR/PLTw3YjiyqU5ll62vStdey/pDtI55k0A59RbwlgpU2sHVGnZ88bY1tj6143l+PZJMBQY5K4IrMZKULeSi8EXGx3OAK60YThyxJBUhegW90SwWc2zg1iD4SbOLx7rqYGx59BYQxPqfQde43NTRNMooXzMYYVhyR4Bh30rP9ejaub3kMqXQOr7xbn80m5dCdHIp7tu/u3hcwYeFfRjkkTis5da5BFT0jR8+C52PugOEV+fNEQLbfme8cR17D4B7QXSMI/FjCsB1/B7U1HmKrGaLzFDNyrLPQkyr2ytC/5EnGMmrKA4JdSCM/IyGHTzH7EpbxwUbLH3BD9GCUuRz6YB2+jxxE5HbbCVU5ru+A4DGhyw0TukZj0UYHOY81wcEnujuOd2lLhx1ZIutRlc6uGbD+4mgvYqGaDoEvBxs13GlMzFucVPu4+vYV3uu6A2DFou/pntSkcuIdizbg8RQLyZ7Rk4kL6T7l3/r+hcma3gZOlHQX1KSPC8tIwowNRO4JXwdREkrkBnbixdMUOAHc5IU4t9Bc8FU7acbA3NDafV32VDiK01zyHDZKJKBjb/tr51sTZ4F+ySFcCbTc2w64lSEzZ5qyhAwI0Ns0jqXR6Nr98pOT4iQkHdEhPfBPpaye4=";

byte[] magic = [0xE0, 0xA8, 0x91, 0xE7, 0xD8, 0xF2, 0x05, 0xAC];

if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
{
    PrintUsage();
    return args.Length == 0 ? 2 : 0;
}

try
{
    if (args[0].Equals("recover", StringComparison.OrdinalIgnoreCase))
        return RecoverWithKnownPair(args);

    return DecryptPath(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

int DecryptPath(string[] commandLine)
{
    bool hasVerb = commandLine[0].Equals("decrypt", StringComparison.OrdinalIgnoreCase);
    int pathIndex = hasVerb ? 1 : 0;
    if (commandLine.Length <= pathIndex)
        throw new ArgumentException("Missing file or directory path.");

    string inputPath = Path.GetFullPath(commandLine[pathIndex]);
    string suffix = DefaultSuffix;
    bool recursive = false;
    bool overwrite = false;

    for (int index = pathIndex + 1; index < commandLine.Length; index++)
    {
        switch (commandLine[index].ToLowerInvariant())
        {
            case "--recursive":
            case "-r":
                recursive = true;
                break;
            case "--overwrite":
                overwrite = true;
                break;
            case "--suffix":
                if (++index >= commandLine.Length)
                    throw new ArgumentException("--suffix requires a value.");
                suffix = commandLine[index];
                break;
            default:
                throw new ArgumentException($"Unknown option: {commandLine[index]}");
        }
    }

    ValidateSuffix(suffix);
    byte[] key = Convert.FromBase64String(EmbeddedKeyBase64);
    if (key.Length != DefaultPeriod)
        throw new InvalidDataException("The embedded key has an unexpected length.");

    if (File.Exists(inputPath))
    {
        if (!TryReadContainer(inputPath, out _))
            throw new InvalidDataException($"Not a supported Esafenet container: {inputPath}");

        string outputPath = BuildOutputPath(inputPath, suffix);
        if (File.Exists(outputPath) && !overwrite)
            throw new IOException($"Output already exists (use --overwrite to replace it): {outputPath}");

        DecryptFile(inputPath, outputPath, key, overwrite);
        Console.WriteLine($"Decrypted: {inputPath}");
        Console.WriteLine($"Output:    {outputPath}");
        return 0;
    }

    if (!Directory.Exists(inputPath))
        throw new FileNotFoundException($"File or directory not found: {inputPath}");

    SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
    string[] candidates = Directory.EnumerateFiles(inputPath, "*", searchOption).ToArray();
    int decrypted = 0;
    int unsupported = 0;
    int existing = 0;
    int failed = 0;

    foreach (string candidate in candidates)
    {
        if (!TryReadContainer(candidate, out _))
        {
            unsupported++;
            continue;
        }

        string outputPath = BuildOutputPath(candidate, suffix);
        if (File.Exists(outputPath) && !overwrite)
        {
            existing++;
            Console.WriteLine($"Exists, skipped: {outputPath}");
            continue;
        }

        try
        {
            DecryptFile(candidate, outputPath, key, overwrite);
            decrypted++;
            Console.WriteLine($"Decrypted: {candidate}");
            Console.WriteLine($"Output:    {outputPath}");
        }
        catch (Exception ex)
        {
            failed++;
            Console.Error.WriteLine($"Failed: {candidate}");
            Console.Error.WriteLine($"  {ex.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Summary: decrypted={decrypted}, existing={existing}, unsupported={unsupported}, failed={failed}");
    return failed == 0 ? 0 : 1;
}

int RecoverWithKnownPair(string[] commandLine)
{
    if (commandLine.Length is < 5 or > 6)
        throw new ArgumentException(
            "Usage: EsafenetRecovery recover <known-plain> <known-encrypted> <input-encrypted> <output> [period]");

    string knownPlainPath = Path.GetFullPath(commandLine[1]);
    string knownEncryptedPath = Path.GetFullPath(commandLine[2]);
    string inputPath = Path.GetFullPath(commandLine[3]);
    string outputPath = Path.GetFullPath(commandLine[4]);
    int period = DefaultPeriod;
    if (commandLine.Length == 6 && (!int.TryParse(commandLine[5], out period) || period <= 0))
        throw new ArgumentException($"Invalid XOR period: {commandLine[5]}");
    if (File.Exists(outputPath))
        throw new IOException($"Refusing to overwrite existing output: {outputPath}");

    byte[] knownPlain = File.ReadAllBytes(knownPlainPath);
    byte[] knownEncrypted = File.ReadAllBytes(knownEncryptedPath);
    ContainerInfo knownInfo = ReadContainerBytes(knownEncrypted, knownEncryptedPath);

    int usableKnownLength = checked((int)Math.Min(
        knownPlain.LongLength,
        Math.Min(knownInfo.OriginalLength, knownEncrypted.LongLength - knownInfo.HeaderLength)));
    if (usableKnownLength < period)
        throw new InvalidDataException(
            $"Known pair has only {usableKnownLength} usable bytes; at least {period} are required.");

    byte[] key = new byte[period];
    double[] confidence = new double[period];
    for (int position = 0; position < period; position++)
    {
        int[] histogram = new int[256];
        int samples = 0;
        for (int offset = position; offset < usableKnownLength; offset += period)
        {
            byte candidate = (byte)(knownPlain[offset] ^ knownEncrypted[knownInfo.HeaderLength + offset]);
            histogram[candidate]++;
            samples++;
        }

        int bestValue = 0;
        int bestCount = -1;
        for (int value = 0; value < histogram.Length; value++)
        {
            if (histogram[value] <= bestCount)
                continue;
            bestValue = value;
            bestCount = histogram[value];
        }

        key[position] = (byte)bestValue;
        confidence[position] = (double)bestCount / samples;
    }

    DecryptFile(inputPath, outputPath, key, overwrite: false);
    Console.WriteLine($"Recovered: {outputPath}");
    Console.WriteLine($"XOR period: {period}");
    Console.WriteLine($"Known-pair usable bytes: {usableKnownLength:N0}");
    Console.WriteLine($"Per-position confidence: min={confidence.Min():P2}, avg={confidence.Average():P2}");
    Console.WriteLine($"First 16 key bytes: {Convert.ToHexString(key.AsSpan(0, Math.Min(16, key.Length)))}");
    return 0;
}

void DecryptFile(string inputPath, string outputPath, byte[] key, bool overwrite)
{
    using FileStream input = new(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    ContainerInfo info = ReadContainerStream(input, inputPath);
    if (input.Length - info.HeaderLength < info.OriginalLength)
        throw new InvalidDataException("Encrypted payload is shorter than the length stored in the header.");

    string outputDirectory = Path.GetDirectoryName(outputPath)!;
    Directory.CreateDirectory(outputDirectory);
    string temporaryPath = outputPath + $".{Guid.NewGuid():N}.partial";

    try
    {
        using (FileStream output = new(
            temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
            FileOptions.SequentialScan))
        {
            input.Position = info.HeaderLength;
            byte[] buffer = new byte[1024 * 1024];
            long processed = 0;
            while (processed < info.OriginalLength)
            {
                int requested = (int)Math.Min(buffer.Length, info.OriginalLength - processed);
                int read = input.Read(buffer, 0, requested);
                if (read == 0)
                    throw new EndOfStreamException("Encrypted payload ended unexpectedly.");

                for (int index = 0; index < read; index++)
                    buffer[index] ^= key[(int)((processed + index) % key.Length)];
                output.Write(buffer, 0, read);
                processed += read;
            }
            output.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, outputPath, overwrite);
    }
    catch
    {
        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);
        throw;
    }
}

ContainerInfo ReadContainerStream(FileStream stream, string path)
{
    Span<byte> header = stackalloc byte[0x18];
    stream.Position = 0;
    stream.ReadExactly(header);
    if (!header[..magic.Length].SequenceEqual(magic))
        throw new InvalidDataException($"Not a supported Esafenet container: {path}");

    int headerLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x0C, 4)));
    long originalLength = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(0x10, 8)));
    if (headerLength <= 0 || headerLength > stream.Length)
        throw new InvalidDataException($"Invalid header length {headerLength}: {path}");
    return new ContainerInfo(headerLength, originalLength);
}

ContainerInfo ReadContainerBytes(byte[] bytes, string path)
{
    if (bytes.Length < 0x18 || !bytes.AsSpan(0, magic.Length).SequenceEqual(magic))
        throw new InvalidDataException($"Not a supported Esafenet container: {path}");

    int headerLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x0C, 4)));
    long originalLength = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x10, 8)));
    if (headerLength <= 0 || headerLength > bytes.Length)
        throw new InvalidDataException($"Invalid header length {headerLength}: {path}");
    return new ContainerInfo(headerLength, originalLength);
}

bool TryReadContainer(string path, out ContainerInfo info)
{
    info = default;
    try
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < 0x18)
            return false;
        info = ReadContainerStream(stream, path);
        return true;
    }
    catch (InvalidDataException)
    {
        return false;
    }
    catch (EndOfStreamException)
    {
        return false;
    }
    catch (UnauthorizedAccessException)
    {
        return false;
    }
    catch (IOException)
    {
        return false;
    }
}

string BuildOutputPath(string inputPath, string suffix)
{
    string? directory = Path.GetDirectoryName(inputPath);
    string extension = Path.GetExtension(inputPath);
    string fileName = Path.GetFileNameWithoutExtension(inputPath);
    return Path.Combine(directory!, fileName + suffix + extension);
}

void ValidateSuffix(string suffix)
{
    if (string.IsNullOrWhiteSpace(suffix))
        throw new ArgumentException("Output suffix cannot be empty.");
    if (suffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
        suffix.Contains(Path.DirectorySeparatorChar) || suffix.Contains(Path.AltDirectorySeparatorChar))
        throw new ArgumentException($"Invalid output suffix: {suffix}");
}

void PrintUsage()
{
    Console.WriteLine("EsafenetRecovery - Esafenet container decryption tool");
    Console.WriteLine();
    Console.WriteLine("Easy mode (the 'decrypt' verb is optional):");
    Console.WriteLine("  EsafenetRecovery <file-or-directory> [--suffix _decrypted] [--recursive] [--overwrite]");
    Console.WriteLine("  EsafenetRecovery decrypt <file-or-directory> [--suffix _decrypted] [--recursive] [--overwrite]");
    Console.WriteLine();
    Console.WriteLine("Known-pair recovery mode:");
    Console.WriteLine("  EsafenetRecovery recover <known-plain> <known-encrypted> <input-encrypted> <output> [period]");
}

readonly record struct ContainerInfo(int HeaderLength, long OriginalLength);
