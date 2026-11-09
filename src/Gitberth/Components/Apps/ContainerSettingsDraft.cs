using System.Globalization;
using System.Text;
using Gitberth.Apps;
using Gitberth.Services;

namespace Gitberth.Components.Apps;

/// <summary>One environment variable as the editor holds it.</summary>
public sealed class EnvRowDraft
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>Set by the image and not edited: shown in "From the image", passed on untouched.</summary>
    public bool FromImage { get; set; }
    public bool Secret { get; set; }
    public bool BuildTime { get; set; }
    /// <summary>Taken out: shown struck through until the change is applied or undone.</summary>
    public bool Removed { get; set; }
    /// <summary>As it was loaded; null for a row added here.</summary>
    public EnvSetting? Original { get; init; }

    public bool IsNew => Original is null;

    public bool Changed => Original is null || Removed || Original.Key != Key || Original.Value != Value || Original.BuildTime != BuildTime || Original.Secret != Secret;

    /// <summary>
    /// Its name or value makes it a secret whatever it's marked (a password-like name, a URL with a password in it): it's
    /// masked, and saved as secret. Any other variable is secret only when it's marked so.
    /// </summary>
    public bool AlwaysSecret => Security.Masking.IsSecret(Key.Trim(), Value);

    /// <summary>Masked wherever it's shown and kept out of logs and the docker run: marked so, or a secret by its name or value.</summary>
    public bool IsSecret => Secret || AlwaysSecret;

    /// <summary>Puts it back as it was loaded.</summary>
    public void Undo()
    {
        if (Original is null) return;
        (Key, Value, FromImage, BuildTime, Secret, Removed) = (Original.Key, Original.Value, Original.FromImage, Original.BuildTime, Original.Secret, false);
    }
}

/// <summary>One mount as the editor holds it.</summary>
public sealed class MountRowDraft
{
    public MountKind Kind { get; set; }
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public bool ReadOnly { get; set; }
    public long? TmpfsBytes { get; set; }
    public bool Removed { get; set; }
    public MountSetting? Original { get; init; }

    public bool IsNew => Original is null;

    public bool Changed => Original is null || Removed || ToSetting() != Original;

    public MountSetting ToSetting() => new(Kind, Kind == MountKind.Tmpfs ? "" : Source.Trim(), Target.Trim(), ReadOnly, Kind == MountKind.Tmpfs ? TmpfsBytes : null);

    public void Undo()
    {
        if (Original is null) return;
        (Kind, Source, Target, ReadOnly, TmpfsBytes, Removed) = (Original.Kind, Original.Source, Original.Target, Original.ReadOnly, Original.TmpfsBytes, false);
    }
}

/// <summary>A label as the editor holds it. Gitberth's and Compose's own are read-only.</summary>
public sealed class LabelRowDraft
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Removed { get; set; }
    public KeyValuePair<string, string>? Original { get; init; }

    public bool ReadOnly => ContainerSettingsDraft.IsReservedLabel(Original?.Key ?? Key);

    public bool IsNew => Original is null;

    public bool Changed => Original is not { } o || Removed || o.Key != Key || o.Value != Value;
}

/// <summary>Memory as the form asks for it: a number and a unit.</summary>
public enum MemoryUnit { MiB, GiB }

/// <summary>
/// The Settings and Environment tabs' editable copy of a container's settings (the contract's
/// <see cref="ContainerSettings"/>): every field as a form holds it (text, numbers, rows), what changed against what was
/// loaded, and the settings it asks for. Nothing in it touches Docker; <see cref="ToSettings"/> is what goes into a
/// <see cref="SettingsEdit"/> as Wanted. Fields the form doesn't show (ports, which the Network tab owns) are passed on
/// exactly as loaded, and so is every field that wasn't edited.
/// </summary>
public sealed class ContainerSettingsDraft
{
    public ContainerSettings Loaded { get; }

    public string Image { get; set; }
    /// <summary>The entrypoint as a command line; empty means the image's own.</summary>
    public string Entrypoint { get; set; }
    /// <summary>The command as a command line; empty means the image's own.</summary>
    public string Command { get; set; }
    public string WorkingDir { get; set; }
    public string User { get; set; }

    public List<EnvRowDraft> Env { get; }
    public List<MountRowDraft> Mounts { get; }
    public List<LabelRowDraft> Labels { get; }

    public string RestartPolicy { get; set; }
    public int MaximumRetryCount { get; set; }

    public double? Memory { get; set; }
    public MemoryUnit MemoryUnit { get; set; }
    public double? Cpus { get; set; }
    public long? CpuShares { get; set; }
    public long? PidsLimit { get; set; }
    /// <summary>Shared memory in MiB; null is Docker's 64 MiB.</summary>
    public long? ShmMiB { get; set; }

    public HealthMode HealthMode { get; set; }
    /// <summary>A custom check's command, run by the shell (CMD-SHELL).</summary>
    public string HealthCommand { get; set; }
    public int? HealthIntervalSeconds { get; set; }
    public int? HealthTimeoutSeconds { get; set; }
    public int? HealthRetries { get; set; }
    public int? HealthStartPeriodSeconds { get; set; }

    public string Hostname { get; set; }
    public string NetworkMode { get; set; }
    /// <summary>One host:ip per line.</summary>
    public string ExtraHosts { get; set; }
    /// <summary>DNS servers, separated by spaces, commas or lines.</summary>
    public string Dns { get; set; }
    public string CapAdd { get; set; }
    public string CapDrop { get; set; }
    public bool Privileged { get; set; }
    public bool ReadOnlyRootfs { get; set; }
    public bool Init { get; set; }
    public string SecurityOpt { get; set; }
    /// <summary>key=value per line.</summary>
    public string Sysctls { get; set; }
    /// <summary>name=soft:hard per line.</summary>
    public string Ulimits { get; set; }
    /// <summary>host:container[:permissions] per line.</summary>
    public string Devices { get; set; }
    public string Gpus { get; set; }
    public string LogDriver { get; set; }
    /// <summary>key=value per line.</summary>
    public string LogOptions { get; set; }
    public string StopSignal { get; set; }
    public int? StopTimeoutSeconds { get; set; }
    public bool Tty { get; set; }
    public bool OpenStdin { get; set; }

    private readonly string loadedEntrypoint, loadedCommand, loadedHealthCommand;
    private readonly (double?, MemoryUnit) loadedMemory;

    private ContainerSettingsDraft(ContainerSettings s)
    {
        Loaded = s;
        Image = s.Image;
        Entrypoint = loadedEntrypoint = s.Entrypoint is null ? "" : ShellLine.Join(s.Entrypoint);
        Command = loadedCommand = s.Command is null ? "" : ShellLine.Join(s.Command);
        WorkingDir = s.WorkingDir ?? "";
        User = s.User ?? "";
        Env = s.Env.Select(e => new EnvRowDraft { Key = e.Key, Value = e.Value, FromImage = e.FromImage, Secret = e.Secret, BuildTime = e.BuildTime, Original = e }).ToList();
        Mounts = s.Mounts.Select(m => new MountRowDraft { Kind = m.Kind, Source = m.Source, Target = m.Target, ReadOnly = m.ReadOnly, TmpfsBytes = m.TmpfsBytes, Original = m }).ToList();
        Labels = s.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => new LabelRowDraft { Key = l.Key, Value = l.Value, Original = l }).ToList();
        RestartPolicy = string.IsNullOrEmpty(s.Restart.Name) ? "no" : s.Restart.Name;
        MaximumRetryCount = s.Restart.MaximumRetryCount;
        (Memory, MemoryUnit) = loadedMemory = MemoryOf(s.Resources.MemoryBytes);
        Cpus = s.Resources.Cpus;
        CpuShares = s.Resources.CpuShares;
        PidsLimit = s.Resources.PidsLimit;
        ShmMiB = s.ShmSizeBytes is long shm ? shm / (1024 * 1024) : null;
        HealthMode = s.Health.Mode;
        HealthCommand = loadedHealthCommand = HealthText(s.Health.Test);
        HealthIntervalSeconds = Seconds(s.Health.Interval);
        HealthTimeoutSeconds = Seconds(s.Health.Timeout);
        HealthRetries = s.Health.Retries;
        HealthStartPeriodSeconds = Seconds(s.Health.StartPeriod);
        Hostname = s.Hostname ?? "";
        NetworkMode = s.NetworkMode;
        ExtraHosts = string.Join('\n', s.ExtraHosts);
        Dns = string.Join(", ", s.Dns);
        CapAdd = string.Join(", ", s.CapAdd);
        CapDrop = string.Join(", ", s.CapDrop);
        Privileged = s.Privileged;
        ReadOnlyRootfs = s.ReadOnlyRootfs;
        Init = s.Init;
        SecurityOpt = string.Join('\n', s.SecurityOpt);
        Sysctls = string.Join('\n', s.Sysctls.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}"));
        Ulimits = string.Join('\n', s.Ulimits.Select(u => $"{u.Name}={u.Soft}:{u.Hard}"));
        Devices = string.Join('\n', s.Devices.Select(d => $"{d.HostPath}:{d.ContainerPath}" + (d.Permissions == "rwm" ? "" : $":{d.Permissions}")));
        Gpus = s.Gpus ?? "";
        LogDriver = s.Logging.Driver;
        LogOptions = string.Join('\n', (s.Logging.Options ?? new Dictionary<string, string>()).OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}"));
        StopSignal = s.StopSignal ?? "";
        StopTimeoutSeconds = s.StopTimeoutSeconds;
        Tty = s.Tty;
        OpenStdin = s.OpenStdin;
    }

    public static ContainerSettingsDraft From(ContainerSettings settings) => new(settings);

    /// <summary>
    /// The draft of a container whose app marks <paramref name="secretKeys"/> secret (its options' SecretKeys): those
    /// variables load marked, so their Secret switch shows what's saved and turning it off is a change.
    /// </summary>
    public static ContainerSettingsDraft From(ContainerSettings settings, IReadOnlyCollection<string> secretKeys) =>
        secretKeys.Count == 0 ? new(settings)
        : new(settings with { Env = settings.Env.Select(e => !e.Secret && secretKeys.Contains(e.Key) ? e with { Secret = true } : e).ToList() });

    /// <summary>
    /// The variable names the app marks secret once the edit is saved (its options' SecretKeys): the loaded ones, plus
    /// every variable added, renamed or switched to secret here (a password-like one is secret on its own), less those
    /// switched off. Untouched variables keep their marking, so an edit elsewhere never changes the list.
    /// </summary>
    public IReadOnlyList<string> SecretKeys(IReadOnlyList<string> loaded)
    {
        var keys = loaded.ToList();
        foreach (var r in Env.Where(r => !r.Removed && r.Key.Trim().Length > 0))
        {
            var key = r.Key.Trim();
            var touched = r.Original is not { } o || o.Key != key || o.Value != r.Value || o.Secret != r.Secret;
            if (!touched) continue;
            if (r.IsSecret && !keys.Contains(key)) keys.Add(key);
            else if (!r.IsSecret) keys.Remove(key);
        }
        return keys;
    }

    /// <summary>Gitberth's and Compose's labels are theirs: shown, never edited.</summary>
    public static bool IsReservedLabel(string key) =>
        key.StartsWith("gitberth.", StringComparison.Ordinal) || key.StartsWith("com.docker.compose.", StringComparison.Ordinal);

    // ---- What it asks for ----

    /// <summary>The settings the form asks for: the loaded ones, with each edited field replaced.</summary>
    public ContainerSettings ToSettings()
    {
        var s = Loaded;
        return s with
        {
            Image = Image.Trim() == s.Image ? s.Image : Image.Trim(),
            Entrypoint = Entrypoint == loadedEntrypoint ? s.Entrypoint : Entrypoint.Trim().Length == 0 ? null : ShellLine.Split(Entrypoint),
            // A command typed over its masked form gets its secrets back; one that still carries the mask is never asked for.
            Command = Command == loadedCommand ? s.Command : Command.Trim().Length == 0 ? null : CommandWords() is var words && HasMask(words) ? s.Command : words,
            WorkingDir = Text(WorkingDir, s.WorkingDir),
            User = Text(User, s.User),
            Env = EnvChanged ? Env.Where(r => !r.Removed && r.Key.Trim().Length > 0)
                .Select(r => r.Original is { } o && !r.Changed ? o : new EnvSetting(r.Key.Trim(), r.Value, FromImage: r.FromImage && !r.Changed, Secret: r.IsSecret, BuildTime: r.BuildTime))
                .ToList() : s.Env,
            Mounts = Mounts.Any(m => m.Changed) ? Mounts.Where(m => !m.Removed && m.Target.Trim().Length > 0).Select(m => m.Changed ? m.ToSetting() : m.Original!).ToList() : s.Mounts,
            Labels = Labels.Any(l => l.Changed) ? Labels.Where(l => !l.Removed && l.Key.Trim().Length > 0).GroupBy(l => l.Key.Trim()).ToDictionary(g => g.Key, g => g.Last().Value) : s.Labels,
            Restart = RestartPolicy == (string.IsNullOrEmpty(s.Restart.Name) ? "no" : s.Restart.Name) && MaximumRetryCount == s.Restart.MaximumRetryCount ? s.Restart
                : new RestartSetting(RestartPolicy, RestartPolicy == "on-failure" ? Math.Max(0, MaximumRetryCount) : 0),
            Resources = ResourcesChanged ? s.Resources with { MemoryBytes = MemoryBytes, Cpus = Cpus is > 0 ? Cpus : null, CpuShares = CpuShares is > 0 ? CpuShares : null, PidsLimit = PidsLimit is 0 ? null : PidsLimit } : s.Resources,
            ShmSizeBytes = ShmMiB == (s.ShmSizeBytes is long shm ? shm / (1024 * 1024) : null) ? s.ShmSizeBytes : ShmMiB is > 0 ? ShmMiB * 1024 * 1024 : null,
            Health = HealthChanged ? NewHealth() : s.Health,
            Hostname = Text(Hostname, s.Hostname),
            NetworkMode = NetworkMode.Trim(),
            ExtraHosts = Lines(ExtraHosts, s.ExtraHosts, '\n'),
            Dns = Lines(Dns, s.Dns, ',', ' ', '\n'),
            CapAdd = Lines(CapAdd, s.CapAdd, ',', ' ', '\n'),
            CapDrop = Lines(CapDrop, s.CapDrop, ',', ' ', '\n'),
            Privileged = Privileged,
            ReadOnlyRootfs = ReadOnlyRootfs,
            Init = Init,
            SecurityOpt = Lines(SecurityOpt, s.SecurityOpt, '\n'),
            Sysctls = Pairs(Sysctls) is var sysctls && SameMap(sysctls, s.Sysctls) ? s.Sysctls : sysctls,
            Ulimits = UlimitsChanged ? ParseUlimits(Ulimits) : s.Ulimits,
            Devices = DevicesChanged ? ParseDevices(Devices) : s.Devices,
            Gpus = Text(Gpus, s.Gpus),
            Logging = LogDriver.Trim() == s.Logging.Driver && SameMap(Pairs(LogOptions), s.Logging.Options ?? new Dictionary<string, string>()) ? s.Logging
                : new LogSetting(LogDriver.Trim(), Pairs(LogOptions)),
            StopSignal = Text(StopSignal, s.StopSignal),
            StopTimeoutSeconds = StopTimeoutSeconds,
            Tty = Tty,
            OpenStdin = OpenStdin,
        };
    }

    // ---- What changed ----

    public bool EnvChanged => Env.Any(r => r.IsNew ? r.Key.Trim().Length > 0 : r.Changed);

    public long? MemoryBytes => (Memory, MemoryUnit) == loadedMemory ? Loaded.Resources.MemoryBytes
        : Memory is > 0 ? (long)Math.Round(Memory.Value * (MemoryUnit == MemoryUnit.GiB ? 1L << 30 : 1L << 20)) : null;

    private bool ResourcesChanged =>
        MemoryBytes != Loaded.Resources.MemoryBytes || Cpus != Loaded.Resources.Cpus || CpuShares != Loaded.Resources.CpuShares || PidsLimit != Loaded.Resources.PidsLimit;

    private bool HealthChanged =>
        HealthMode != Loaded.Health.Mode || HealthCommand != loadedHealthCommand || HealthIntervalSeconds != Seconds(Loaded.Health.Interval)
        || HealthTimeoutSeconds != Seconds(Loaded.Health.Timeout) || HealthRetries != Loaded.Health.Retries || HealthStartPeriodSeconds != Seconds(Loaded.Health.StartPeriod);

    private bool UlimitsChanged => !ParseUlimits(Ulimits).SequenceEqual(Loaded.Ulimits);

    private bool DevicesChanged => !ParseDevices(Devices).SequenceEqual(Loaded.Devices);

    /// <summary>
    /// The settings that differ from what was loaded, by field: what the tab badges, the section dots and the apply bar
    /// count. Compares what the form asks for with what was loaded, so typing a value and typing it back is no change.
    /// </summary>
    public IReadOnlySet<SettingField> ChangedFields()
    {
        var a = Loaded;
        var b = ToSettings();
        var fields = new HashSet<SettingField>();
        void Check(SettingField field, bool same) { if (!same) fields.Add(field); }
        Check(SettingField.Image, a.Image == b.Image);
        Check(SettingField.Entrypoint, Same(a.Entrypoint, b.Entrypoint));
        Check(SettingField.Command, Same(a.Command, b.Command));
        Check(SettingField.WorkingDir, a.WorkingDir == b.WorkingDir);
        Check(SettingField.User, a.User == b.User);
        Check(SettingField.Env, a.Env.SequenceEqual(b.Env));
        Check(SettingField.Mounts, a.Mounts.SequenceEqual(b.Mounts));
        Check(SettingField.Labels, SameMap(a.Labels, b.Labels));
        Check(SettingField.RestartPolicy, a.Restart == b.Restart);
        Check(SettingField.Memory, a.Resources.MemoryBytes == b.Resources.MemoryBytes);
        Check(SettingField.Cpu, a.Resources.Cpus == b.Resources.Cpus && a.Resources.CpuShares == b.Resources.CpuShares);
        Check(SettingField.PidsLimit, a.Resources.PidsLimit == b.Resources.PidsLimit);
        Check(SettingField.ShmSize, a.ShmSizeBytes == b.ShmSizeBytes);
        Check(SettingField.Health, a.Health == b.Health || (a.Health.Mode == b.Health.Mode && Same(a.Health.Test, b.Health.Test)
            && a.Health.Interval == b.Health.Interval && a.Health.Timeout == b.Health.Timeout && a.Health.Retries == b.Health.Retries
            && a.Health.StartPeriod == b.Health.StartPeriod && a.Health.StartInterval == b.Health.StartInterval));
        Check(SettingField.Hostname, a.Hostname == b.Hostname && a.Domainname == b.Domainname);
        Check(SettingField.NetworkMode, a.NetworkMode == b.NetworkMode);
        Check(SettingField.ExtraHosts, a.ExtraHosts.SequenceEqual(b.ExtraHosts));
        Check(SettingField.Dns, a.Dns.SequenceEqual(b.Dns));
        Check(SettingField.Capabilities, a.CapAdd.SequenceEqual(b.CapAdd) && a.CapDrop.SequenceEqual(b.CapDrop));
        Check(SettingField.Privileged, a.Privileged == b.Privileged);
        Check(SettingField.ReadOnlyRootfs, a.ReadOnlyRootfs == b.ReadOnlyRootfs);
        Check(SettingField.Init, a.Init == b.Init);
        Check(SettingField.SecurityOpt, a.SecurityOpt.SequenceEqual(b.SecurityOpt));
        Check(SettingField.Sysctls, SameMap(a.Sysctls, b.Sysctls));
        Check(SettingField.Ulimits, a.Ulimits.SequenceEqual(b.Ulimits));
        Check(SettingField.Devices, a.Devices.SequenceEqual(b.Devices));
        Check(SettingField.Gpus, a.Gpus == b.Gpus);
        Check(SettingField.Logging, a.Logging.Driver == b.Logging.Driver && SameMap(a.Logging.Options ?? new Dictionary<string, string>(), b.Logging.Options ?? new Dictionary<string, string>()));
        Check(SettingField.Stop, a.StopSignal == b.StopSignal && a.StopTimeoutSeconds == b.StopTimeoutSeconds);
        Check(SettingField.Terminal, a.Tty == b.Tty && a.OpenStdin == b.OpenStdin);
        return fields;
    }

    public bool Dirty => ChangedFields().Count > 0;

    // ---- The command, when it carries a secret ----

    /// <summary>Its loaded command carries a secret (cloudflared's --token): the field shows it masked.</summary>
    public bool CommandHasSecrets => Loaded.Command is { } cmd && !Security.Masking.Args(cmd).SequenceEqual(cmd);

    /// <summary>The command as its field shows it: masked while it carries a secret and hasn't been typed over; else as typed.</summary>
    public string CommandShown => CommandHasSecrets && Command == loadedCommand ? ShellLine.Join(Security.Masking.Args(Loaded.Command!)) : Command;

    /// <summary>
    /// What's wrong with the command as typed: it still has the mask (••••) somewhere Gitberth can't give the hidden value
    /// back, so applying it would write the mask in as the secret. Null when it's fine.
    /// </summary>
    public string? CommandProblem => Command != loadedCommand && Command.Trim().Length > 0 && HasMask(CommandWords())
        ? $"It still has {Security.Masking.Mask} where a secret was hidden. Type the secret itself there, or undo the command."
        : null;

    /// <summary>Everything that stops the draft from being applied: the variables' problems and the command's.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = EnvProblems().ToList();
        if (CommandProblem is { } command) problems.Add(command);
        return problems;
    }

    /// <summary>
    /// A command line typed over a masked one (<see cref="Security.Masking.Args"/>), each masked word given back its
    /// loaded value: the word at the same place, else the one masked word it can only be when the line moved. A bare
    /// mask (the value after --token) is only given back after the same option, so a secret never moves to another one.
    /// Words that still carry the mask are left as they are, for <see cref="CommandProblem"/> to refuse.
    /// </summary>
    public static List<string> Unmask(IReadOnlyList<string> typed, IReadOnlyList<string> loaded)
    {
        var masked = Security.Masking.Args(loaded);
        var result = new List<string>(typed.Count);
        for (var i = 0; i < typed.Count; i++)
        {
            var word = typed[i];
            if (!word.Contains(Security.Masking.Mask, StringComparison.Ordinal))
            {
                result.Add(word);
                continue;
            }
            var at = i;
            bool Fits(int j) => masked[j] == word && loaded[j] != word
                && (word != Security.Masking.Mask || (at > 0 && j > 0 && typed[at - 1] == masked[j - 1]));
            if (i < masked.Count && Fits(i))
            {
                result.Add(loaded[i]);
                continue;
            }
            var places = Enumerable.Range(0, masked.Count).Where(Fits).ToList();
            result.Add(places.Count == 1 ? loaded[places[0]] : word);
        }
        return result;
    }

    // The command's words as typed, with the secrets the mask hid given back.
    private List<string> CommandWords()
    {
        var words = ShellLine.Split(Command);
        return Loaded.Command is { } loaded ? Unmask(words, loaded) : words;
    }

    private static bool HasMask(IEnumerable<string> words) => words.Any(w => w.Contains(Security.Masking.Mask, StringComparison.Ordinal));

    // ---- Rows ----

    public EnvRowDraft AddEnv(string key = "", string value = "")
    {
        var row = new EnvRowDraft { Key = key, Value = value, Secret = Security.Masking.IsSecretName(key) };
        Env.Add(row);
        return row;
    }

    /// <summary>Takes a row out: a new one goes at once; a loaded one stays, struck through, until it's applied or undone.</summary>
    public void RemoveEnv(EnvRowDraft row)
    {
        if (row.IsNew) Env.Remove(row);
        else row.Removed = true;
    }

    /// <summary>Pastes a .env file: each KEY=value replaces the variable of that name, or is added. Returns how many it read.</summary>
    public int PasteEnv(string text)
    {
        var read = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
            if (Env.FirstOrDefault(r => r.Key == key && !r.Removed) is { } existing)
            {
                existing.Value = value;
                existing.FromImage = false;
            }
            else AddEnv(key, value);
            read++;
        }
        return read;
    }

    /// <summary>A variable can't be named twice, and needs a name.</summary>
    public IReadOnlyList<string> EnvProblems()
    {
        var live = Env.Where(r => !r.Removed).ToList();
        var problems = new List<string>();
        if (live.Any(r => r.Key.Trim().Length == 0 && r.Value.Length > 0)) problems.Add("A variable has a value but no name.");
        foreach (var dup in live.Where(r => r.Key.Trim().Length > 0).GroupBy(r => r.Key.Trim()).Where(g => g.Count() > 1))
            problems.Add($"{dup.Key} is set twice.");
        foreach (var bad in live.Where(r => r.Key.Contains(' ') || r.Key.Contains('=')))
            problems.Add($"“{bad.Key}” can't be a variable's name: no spaces or =.");
        return problems;
    }

    public MountRowDraft AddMount(MountKind kind)
    {
        var row = new MountRowDraft { Kind = kind };
        Mounts.Add(row);
        return row;
    }

    public void RemoveMount(MountRowDraft row)
    {
        if (row.IsNew) Mounts.Remove(row);
        else row.Removed = true;
    }

    public LabelRowDraft AddLabel()
    {
        var row = new LabelRowDraft();
        Labels.Add(row);
        return row;
    }

    public void RemoveLabel(LabelRowDraft row)
    {
        if (row.IsNew) Labels.Remove(row);
        else row.Removed = true;
    }

    // ---- Undo, per field ----

    public void UndoImage() => Image = Loaded.Image;

    public void UndoCommand() => (Entrypoint, Command) = (loadedEntrypoint, loadedCommand);

    public void UndoEnv()
    {
        Env.RemoveAll(r => r.IsNew);
        foreach (var r in Env) r.Undo();
    }

    public void UndoRestart() => (RestartPolicy, MaximumRetryCount) = (string.IsNullOrEmpty(Loaded.Restart.Name) ? "no" : Loaded.Restart.Name, Loaded.Restart.MaximumRetryCount);

    public void UndoMemory() => (Memory, MemoryUnit) = MemoryOf(Loaded.Resources.MemoryBytes);

    public void UndoCpu() => (Cpus, CpuShares) = (Loaded.Resources.Cpus, Loaded.Resources.CpuShares);

    // ---- Helpers ----

    /// <summary>A memory limit in the unit that reads best: 512 MiB, 1.5 GiB.</summary>
    public static (double? Value, MemoryUnit Unit) MemoryOf(long? bytes) =>
        bytes is not long b || b <= 0 ? (null, MemoryUnit.MiB)
        : b >= 1L << 30 && b % (1L << 28) == 0 ? (b / (double)(1L << 30), MemoryUnit.GiB)
        : (Math.Round(b / (double)(1L << 20), 1), MemoryUnit.MiB);

    private HealthSetting NewHealth() => HealthMode switch
    {
        HealthMode.Custom => new HealthSetting(HealthMode.Custom, HealthCommand.Trim().Length == 0 ? null : ["CMD-SHELL", HealthCommand.Trim()],
            Span(HealthIntervalSeconds), Span(HealthTimeoutSeconds), HealthRetries, Span(HealthStartPeriodSeconds), Loaded.Health.StartInterval),
        HealthMode.Disabled => new HealthSetting(HealthMode.Disabled),
        _ => new HealthSetting(),
    };

    /// <summary>A check's test as the form shows it: the shell command, or the words of a CMD.</summary>
    public static string HealthText(IReadOnlyList<string>? test) => test switch
    {
        null or [] => "",
        ["CMD-SHELL", var command, ..] => command,
        ["CMD", ..] => ShellLine.Join(test.Skip(1)),
        ["NONE", ..] => "",
        _ => ShellLine.Join(test),
    };

    private static TimeSpan? Span(int? seconds) => seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : null;

    private static int? Seconds(TimeSpan? span) => span is { } s ? (int)s.TotalSeconds : null;

    private static string? Text(string edited, string? loaded) => edited == (loaded ?? "") ? loaded : edited.Trim().Length == 0 ? null : edited.Trim();

    private static IReadOnlyList<string> Lines(string text, IReadOnlyList<string> loaded, params char[] separators)
    {
        var items = text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return items.SequenceEqual(loaded) ? loaded : items;
    }

    private static Dictionary<string, string> Pairs(string text) =>
        text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2)).Where(p => p[0].Trim().Length > 0)
            .GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.Last().Length > 1 ? g.Last()[1].Trim() : "");

    private static List<UlimitSetting> ParseUlimits(string text) =>
        text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
            .Select(p =>
            {
                var limits = p[1].Split(':');
                var soft = long.TryParse(limits[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var s) ? s : 0;
                var hard = limits.Length > 1 && long.TryParse(limits[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var h) ? h : soft;
                return new UlimitSetting(p[0].Trim(), soft, hard);
            }).ToList();

    private static List<DeviceSetting> ParseDevices(string text) =>
        text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split(':')).Where(p => p[0].Length > 0)
            .Select(p => new DeviceSetting(p[0], p.Length > 1 && p[1].Length > 0 ? p[1] : p[0], p.Length > 2 ? p[2] : "rwm")).ToList();

    private static bool Same(IReadOnlyList<string>? a, IReadOnlyList<string>? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);

    private static bool SameMap(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);
}

/// <summary>A command line and its words, the way a shell splits it: quotes keep spaces, a backslash escapes.</summary>
public static class ShellLine
{
    public static string Join(IEnumerable<string> words) => string.Join(' ', words.Select(Quote));

    public static string Quote(string word) =>
        word.Length == 0 ? "\"\""
        : word.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '\\') ? "\"" + word.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        : word;

    public static List<string> Split(string line)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var inWord = false;
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is null && char.IsWhiteSpace(c))
            {
                if (inWord) words.Add(current.ToString());
                current.Clear();
                inWord = false;
                continue;
            }
            inWord = true;
            if (c == '\\' && i + 1 < line.Length && quote != '\'')
            {
                current.Append(line[++i]);
            }
            else if (quote is null && c is '"' or '\'')
            {
                quote = c;
            }
            else if (quote == c)
            {
                quote = null;
            }
            else
            {
                current.Append(c);
            }
        }
        if (inWord) words.Add(current.ToString());
        return words;
    }
}

/// <summary>What Gitberth keeps about an app (the contract's <see cref="AppOptions"/>), as the General section edits it.</summary>
public sealed class AppOptionsDraft
{
    public AppOptions Loaded { get; }
    public string Name { get; set; }
    public bool Pinned { get; set; }
    public bool GatherStats { get; set; }
    public string HealthCheckPath { get; set; }
    public UpdatePolicy Updates { get; set; }
    /// <summary>The scheduled restart in minutes; 0 for none.</summary>
    public int RestartAmount { get; set; }
    public RestartUnit RestartUnit { get; set; }
    public bool RestartOn { get; set; }

    private AppOptionsDraft(AppOptions o)
    {
        Loaded = o;
        Name = o.Name;
        Pinned = o.Pinned;
        GatherStats = o.GatherStats;
        HealthCheckPath = o.HealthCheckPath ?? "";
        Updates = o.Updates;
        RestartOn = o.ScheduledRestart is not null;
        (RestartAmount, RestartUnit) = o.ScheduledRestart is { } r ? (r.Amount, r.Unit) : (6, RestartUnit.Hours);
    }

    public static AppOptionsDraft From(AppOptions options) => new(options);

    public AppOptions ToOptions() => Loaded with
    {
        Name = Name.Trim().Length == 0 ? Loaded.Name : Name.Trim(),
        Pinned = Pinned,
        GatherStats = GatherStats,
        HealthCheckPath = HealthCheckPath.Trim() == (Loaded.HealthCheckPath ?? "") ? Loaded.HealthCheckPath : HealthCheckPath.Trim().Length == 0 ? null : HealthCheckPath.Trim(),
        Updates = Updates,
        ScheduledRestart = !RestartOn ? null : Loaded.ScheduledRestart is { } r && r.Amount == RestartAmount && r.Unit == RestartUnit ? r : new RestartInterval(Math.Max(1, RestartAmount), RestartUnit),
    };

    /// <summary>The names of what changed: "name", "pinned"… for the apply bar's count.</summary>
    public IReadOnlyList<string> Changed()
    {
        var a = Loaded;
        var b = ToOptions();
        var changed = new List<string>();
        if (a.Name != b.Name) changed.Add("name");
        if (a.Pinned != b.Pinned) changed.Add("pinned");
        if (a.GatherStats != b.GatherStats) changed.Add("stats");
        if (a.HealthCheckPath != b.HealthCheckPath) changed.Add("health URL");
        if (a.Updates != b.Updates) changed.Add("updates");
        if (a.ScheduledRestart != b.ScheduledRestart) changed.Add("scheduled restart");
        return changed;
    }

    public bool Dirty => Changed().Count > 0;

    public string? NameProblem => Name.Trim().Length == 0 ? "An app needs a name." : Name.Trim().Length > 80 ? "That's a long name: 80 characters at most." : null;
}

/// <summary>A Git app's code and build settings (the contract's <see cref="GitOptions"/>), as the Source section edits them.</summary>
public sealed class GitOptionsDraft
{
    public GitOptions Loaded { get; }
    public string Branch { get; set; }
    public string Subdirectory { get; set; }
    public Data.DockerfileMode DockerfileMode { get; set; }
    public string CustomDockerfile { get; set; }
    public string ProjectHint { get; set; }
    public bool AutoDeploy { get; set; }
    public bool VercelVariables { get; set; }

    private GitOptionsDraft(GitOptions g)
    {
        Loaded = g;
        Branch = g.Branch;
        Subdirectory = g.Subdirectory ?? "";
        DockerfileMode = g.DockerfileMode;
        CustomDockerfile = g.CustomDockerfile ?? "";
        ProjectHint = g.ProjectHint ?? "";
        AutoDeploy = g.AutoDeploy;
        VercelVariables = g.VercelVariables;
    }

    public static GitOptionsDraft From(GitOptions options) => new(options);

    public GitOptions ToOptions() => new(Branch.Trim().Length == 0 ? Loaded.Branch : Branch.Trim(),
        Subdirectory.Trim().Trim('/') is { Length: > 0 } sub ? sub : null, DockerfileMode,
        CustomDockerfile.Length == 0 ? null : CustomDockerfile, ProjectHint.Trim().Length == 0 ? null : ProjectHint.Trim(), AutoDeploy, VercelVariables);

    public bool Dirty => ToOptions() != Loaded;

    /// <summary>Changing the branch, folder or how it's built needs a new build.</summary>
    public bool NeedsBuild
    {
        get
        {
            var b = ToOptions();
            return b.Branch != Loaded.Branch || b.Subdirectory != Loaded.Subdirectory || b.DockerfileMode != Loaded.DockerfileMode
                || b.CustomDockerfile != Loaded.CustomDockerfile || b.ProjectHint != Loaded.ProjectHint;
        }
    }
}
