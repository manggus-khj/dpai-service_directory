using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using DEEPAi.ServiceDirectory.Domain;

namespace DEEPAi.ServiceDirectory.Infrastructure.Logging
{
    public sealed class SystemLogRetentionAfterWriteException : IOException
    {
        internal SystemLogRetentionAfterWriteException(Exception innerException)
            : base(
                "The system log event was written, but retention cleanup failed.",
                innerException)
        {
        }

        public bool EventWritten => true;
    }

    public sealed class SystemFileLogger
    {
        public const int MinimumRetentionDays = 1;
        public const int DefaultRetentionDays = 30;
        public const int MaximumRetentionDays = 1095;

        private const string LogsDirectoryName = "logs";
        private const string SystemDirectoryName = "system";
        private const string FileNamePrefix = "dpai-sd_";
        private const string FileNameSuffix = ".log";
        private const string FileDateFormat = "yyyy-MM-dd";
        private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Regex SystemLogFileNamePattern = new Regex(
            @"^dpai-sd_(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})\.log$",
            RegexOptions.CultureInvariant);

        private readonly Func<DateTimeOffset> _localNowProvider;
        private readonly string _dataRootPath;
        private readonly string _logDirectoryPath;
        private readonly object _syncRoot = new object();
        private DateTime? _lastCleanedLocalDate;
        private int? _lastCleanedRetentionDays;

        public SystemFileLogger()
            : this(GetDefaultDataRootPath(), () => DateTimeOffset.Now)
        {
        }

        internal SystemFileLogger(
            string dataRootPath,
            Func<DateTimeOffset> localNowProvider)
        {
            if (string.IsNullOrWhiteSpace(dataRootPath))
            {
                throw new ArgumentException("Data root path is required.", nameof(dataRootPath));
            }

            if (!IsFullyQualifiedLocalPath(dataRootPath))
            {
                throw new ArgumentException(
                    "Data root path must be a fully qualified local drive path.",
                    nameof(dataRootPath));
            }

            if (localNowProvider == null)
            {
                throw new ArgumentNullException(nameof(localNowProvider));
            }

            string fullPath = Path.GetFullPath(dataRootPath);
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                || fullPath.StartsWith("//", StringComparison.Ordinal))
            {
                throw new NotSupportedException("System logs must be stored on a local path.");
            }

            string pathRoot = Path.GetPathRoot(fullPath);
            var drive = new DriveInfo(pathRoot);
            if (drive.DriveType == DriveType.Network)
            {
                throw new NotSupportedException(
                    "System logs must not be stored on a mapped network drive.");
            }

            _dataRootPath = TrimTrailingDirectorySeparators(fullPath);
            string volumeRoot = TrimTrailingDirectorySeparators(pathRoot);
            if (StringComparer.OrdinalIgnoreCase.Equals(_dataRootPath, volumeRoot))
            {
                throw new ArgumentException(
                    "A volume root cannot be used as the data root.",
                    nameof(dataRootPath));
            }

            _logDirectoryPath = Path.Combine(
                _dataRootPath,
                LogsDirectoryName,
                SystemDirectoryName);
            _localNowProvider = localNowProvider;

            EnsureLogDirectoryIsSafe();
        }

        public void WriteServiceStarted(Guid instanceId, int retentionDays)
        {
            EnsureNonEmptyGuid(instanceId, nameof(instanceId));
            Write(
                SystemLogEventCode.ServiceStarted,
                "InstanceId=" + instanceId.ToString("D"),
                retentionDays);
        }

        public void WriteServiceStopped(string reason, int retentionDays)
        {
            Write(
                SystemLogEventCode.ServiceStopped,
                "Reason=" + ValidateDetailToken(reason, nameof(reason)),
                retentionDays);
        }

        public void WriteRegisteredServiceCreated(
            ProductCode productCode,
            int retentionDays)
        {
            WriteRegisteredServiceEvent(
                SystemLogEventCode.RegisteredServiceCreated,
                productCode,
                retentionDays);
        }

        public void WriteRegisteredServiceUpdated(
            ProductCode productCode,
            int retentionDays)
        {
            WriteRegisteredServiceEvent(
                SystemLogEventCode.RegisteredServiceUpdated,
                productCode,
                retentionDays);
        }

        public void WriteRegisteredServiceDeleted(
            ProductCode productCode,
            int retentionDays)
        {
            WriteRegisteredServiceEvent(
                SystemLogEventCode.RegisteredServiceDeleted,
                productCode,
                retentionDays);
        }

        public void WriteSyncInitialStarted(
            Guid peerInstanceId,
            string trigger,
            int retentionDays)
        {
            WriteSyncEvent(
                SystemLogEventCode.SyncInitialStarted,
                peerInstanceId,
                "Trigger",
                trigger,
                retentionDays);
        }

        public void WriteSyncStarted(
            Guid peerInstanceId,
            string trigger,
            int retentionDays)
        {
            WriteSyncEvent(
                SystemLogEventCode.SyncStarted,
                peerInstanceId,
                "Trigger",
                trigger,
                retentionDays);
        }

        public void WriteSyncStopped(
            Guid peerInstanceId,
            string reason,
            int retentionDays)
        {
            WriteSyncEvent(
                SystemLogEventCode.SyncStopped,
                peerInstanceId,
                "Reason",
                reason,
                retentionDays);
        }

        public void WriteSyncSucceeded(
            Guid peerInstanceId,
            string trigger,
            int retentionDays)
        {
            WriteSyncEvent(
                SystemLogEventCode.SyncSucceeded,
                peerInstanceId,
                "Trigger",
                trigger,
                retentionDays);
        }

        private void Write(
            SystemLogEventCode eventCode,
            string details,
            int retentionDays)
        {
            string fileEventCode = SystemLogEventCodeFormatter.ToFileCode(eventCode);
            ValidateDetails(details);
            ValidateRetentionDays(retentionDays);

            lock (_syncRoot)
            {
                DateTimeOffset localNow = _localNowProvider();
                DateTime localDate = localNow.Date;
                EnsureLogDirectoryIsSafe();

                string fileName = FileNamePrefix
                    + localNow.ToString(FileDateFormat, CultureInfo.InvariantCulture)
                    + FileNameSuffix;
                string logFilePath = Path.Combine(_logDirectoryPath, fileName);
                EnsureLogFileIsSafe(logFilePath, true);

                string line = localNow.ToString(TimestampFormat, CultureInfo.InvariantCulture)
                    + " ["
                    + fileEventCode
                    + "] "
                    + details
                    + Environment.NewLine;
                byte[] encodedLine = StrictUtf8.GetBytes(line);

                using (var stream = new FileStream(
                    logFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(encodedLine, 0, encodedLine.Length);
                    stream.Flush(true);
                }

                if (_lastCleanedLocalDate != localDate
                    || _lastCleanedRetentionDays != retentionDays)
                {
                    try
                    {
                        CleanExpiredFiles(localDate, retentionDays);
                    }
                    catch (Exception exception) when (IsRetentionIoFailure(exception))
                    {
                        throw new SystemLogRetentionAfterWriteException(exception);
                    }

                    _lastCleanedLocalDate = localDate;
                    _lastCleanedRetentionDays = retentionDays;
                }
            }
        }

        public void ApplyRetention(int retentionDays)
        {
            ValidateRetentionDays(retentionDays);

            lock (_syncRoot)
            {
                DateTimeOffset localNow = _localNowProvider();
                DateTime localDate = localNow.Date;
                EnsureLogDirectoryIsSafe();
                CleanExpiredFiles(localDate, retentionDays);
                _lastCleanedLocalDate = localDate;
                _lastCleanedRetentionDays = retentionDays;
            }
        }

        private void CleanExpiredFiles(DateTime localDate, int retentionDays)
        {
            DateTime oldestRetainedDate = GetOldestRetainedDate(localDate, retentionDays);

            foreach (string filePath in Directory.EnumerateFiles(
                _logDirectoryPath,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(filePath);
                Match match = SystemLogFileNamePattern.Match(fileName);
                if (!match.Success)
                {
                    continue;
                }

                DateTime fileDate;
                if (!DateTime.TryParseExact(
                    match.Groups["date"].Value,
                    FileDateFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out fileDate))
                {
                    continue;
                }

                EnsureLogFileIsSafe(filePath, false);
                if (fileDate >= oldestRetainedDate)
                {
                    continue;
                }

                EnsureLogFileIsSafe(filePath, false);
                File.Delete(filePath);
            }
        }

        private void EnsureLogDirectoryIsSafe()
        {
            var dataRoot = new DirectoryInfo(_dataRootPath);
            if (!dataRoot.Exists)
            {
                throw new DirectoryNotFoundException(
                    "The installer-provisioned service directory data root does not exist.");
            }

            var logDirectory = new DirectoryInfo(_logDirectoryPath);
            if (!logDirectory.Exists)
            {
                throw new DirectoryNotFoundException(
                    "The installer-provisioned system log directory does not exist.");
            }

            for (DirectoryInfo current = logDirectory;
                current != null;
                current = current.Parent)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "The system log path and its parents must not be reparse points.");
                }
            }
        }

        private static DateTime GetOldestRetainedDate(
            DateTime localDate,
            int retentionDays)
        {
            long precedingDaysToRetain = (long)retentionDays - 1L;
            long daysSinceMinimum = localDate.Ticks / TimeSpan.TicksPerDay;
            if (precedingDaysToRetain >= daysSinceMinimum)
            {
                return DateTime.MinValue;
            }

            return localDate.AddDays(-precedingDaysToRetain);
        }

        private static void ValidateDetails(string details)
        {
            if (details == null)
            {
                throw new ArgumentNullException(nameof(details));
            }

            if (string.IsNullOrWhiteSpace(details))
            {
                throw new ArgumentException(
                    "System log details must contain non-whitespace text.",
                    nameof(details));
            }

            for (int index = 0; index < details.Length; index++)
            {
                char current = details[index];
                if (char.IsHighSurrogate(current))
                {
                    if (index + 1 >= details.Length
                        || !char.IsLowSurrogate(details[index + 1]))
                    {
                        throw new ArgumentException(
                            "System log details contain an invalid Unicode surrogate.",
                            nameof(details));
                    }

                    index++;
                    continue;
                }

                if (char.IsLowSurrogate(current))
                {
                    throw new ArgumentException(
                        "System log details contain an invalid Unicode surrogate.",
                        nameof(details));
                }

                if (char.IsControl(current)
                    || current == '\u2028'
                    || current == '\u2029')
                {
                    throw new ArgumentException(
                        "System log details must contain exactly one text line.",
                        nameof(details));
                }
            }
        }

        private static void ValidateRetentionDays(int retentionDays)
        {
            if (retentionDays < MinimumRetentionDays
                || retentionDays > MaximumRetentionDays)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(retentionDays),
                    retentionDays,
                    "Log retention days must be between 1 and 1095.");
            }
        }

        private void WriteRegisteredServiceEvent(
            SystemLogEventCode eventCode,
            ProductCode productCode,
            int retentionDays)
        {
            if (!productCode.IsValid)
            {
                throw new ArgumentException(
                    "Product code must be valid.",
                    nameof(productCode));
            }

            Write(
                eventCode,
                "ProductCode=" + productCode.Value,
                retentionDays);
        }

        private void WriteSyncEvent(
            SystemLogEventCode eventCode,
            Guid peerInstanceId,
            string detailName,
            string detailValue,
            int retentionDays)
        {
            EnsureNonEmptyGuid(peerInstanceId, nameof(peerInstanceId));
            Write(
                eventCode,
                "PeerInstanceId="
                    + peerInstanceId.ToString("D")
                    + " "
                    + detailName
                    + "="
                    + ValidateDetailToken(detailValue, nameof(detailValue)),
                retentionDays);
        }

        private static void EnsureNonEmptyGuid(Guid value, string parameterName)
        {
            if (value == Guid.Empty)
            {
                throw new ArgumentException("GUID must not be empty.", parameterName);
            }
        }

        private static string ValidateDetailToken(string value, string parameterName)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64)
            {
                throw new ArgumentException(
                    "A detail token must contain between 1 and 64 characters.",
                    parameterName);
            }

            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                bool isUpperAsciiLetter = current >= 'A' && current <= 'Z';
                bool isAsciiDigit = current >= '0' && current <= '9';
                if (!isUpperAsciiLetter && !isAsciiDigit && current != '_')
                {
                    throw new ArgumentException(
                        "A detail token may contain only A-Z, 0-9, and underscore.",
                        parameterName);
                }
            }

            return value;
        }

        private static bool IsFullyQualifiedLocalPath(string path)
        {
            return path.Length >= 3
                && ((path[0] >= 'A' && path[0] <= 'Z')
                    || (path[0] >= 'a' && path[0] <= 'z'))
                && path[1] == Path.VolumeSeparatorChar
                && (path[2] == Path.DirectorySeparatorChar
                    || path[2] == Path.AltDirectorySeparatorChar);
        }

        private static string TrimTrailingDirectorySeparators(string path)
        {
            return path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        private static string GetDefaultDataRootPath()
        {
            string commonApplicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(commonApplicationData))
            {
                throw new InvalidOperationException(
                    "The common application data path is unavailable.");
            }

            return Path.Combine(
                commonApplicationData,
                "DEEPAi",
                "ServiceDirectory");
        }

        private static bool IsRetentionIoFailure(Exception exception)
        {
            return exception is IOException
                || exception is UnauthorizedAccessException
                || exception is SecurityException;
        }

        private static void EnsureLogFileIsSafe(string filePath, bool allowMissing)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(filePath);
            }
            catch (FileNotFoundException) when (allowMissing)
            {
                return;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new IOException("A system log file path must not be a directory.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("System log files must not be reparse points.");
            }
        }
    }
}
