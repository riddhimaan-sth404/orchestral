using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Orchestral.Desktop.Services;

namespace Orchestral.Desktop;

public class ChatSessionItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled Session";
    public string WorkspaceFolder { get; set; } = "/home/admin/Swarm";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public bool IsLoadedFromDb { get; set; } = false;
    public List<Control> MessageCards { get; set; } = new();
}

public class WorkspaceGroup
{
    public string FolderPath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsExpanded { get; set; } = true;
    public List<ChatSessionItem> Sessions { get; set; } = new();
}

public class SubagentActivityItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Status { get; set; } = "Idle";
    public string CurrentTool { get; set; } = "";
    public string Target { get; set; } = "127.0.0.1";
    public string RecentOutput { get; set; } = "";
}

public partial class MainWindow : Window
{
    private readonly CitadelService _citadel;
    private readonly DispatcherTimer _telemetryTimer;
    private readonly string _orchestralBin;
    private readonly List<string> _attachedContexts = new();
    private int _lastSelectedModelIndex = 0;

    // Scope & Directory Sandboxing
    private bool _isGlobalScope = false;

    // Chrome MCP Server Integration
    private bool _chromeMcpEnabled = true;

    // Workspace Sessions
    private readonly List<WorkspaceGroup> _workspaceGroups = new();
    private ChatSessionItem? _activeSession;

    // Background Tasks & Subagent Activities
    private readonly List<string> _backgroundTasks = new();
    private readonly List<SubagentActivityItem> _subagents = new();

    public MainWindow()
    {
        InitializeComponent();
        _citadel = new CitadelService();
        _orchestralBin = ResolveOrchestralBinary();

        WorkspaceLocationText.Text = $"Location: {Directory.GetCurrentDirectory()}";
        if (FsMcpRootBox != null)
        {
            FsMcpRootBox.Text = Directory.GetCurrentDirectory();
        }

        // Intercept Enter key at the Tunneling (Preview) stage so Enter sends and Shift+Enter inserts a newline
        ChatInput.AddHandler(InputElement.KeyDownEvent, OnChatInputKeyDown, RoutingStrategies.Tunnel);

        InitSubagents();
        InitWorkspaceSessions();

        _telemetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _telemetryTimer.Tick += async (s, e) => await RefreshTelemetryAsync();
        _telemetryTimer.Start();

        LogConsole("Orchestral workspace session initialized.");
        LogConsole("Connected to Citadel operations daemon at http://127.0.0.1:8088.");

        _ = RefreshTelemetryAsync();
    }

    private void InitSubagents()
    {
        _subagents.Add(new SubagentActivityItem
        {
            Id = "sentinel-01",
            Name = "Sentinel Recon",
            Role = "sentinel_recon",
            Status = "Idle",
            Target = "None",
            CurrentTool = "Idle",
            RecentOutput = "No active execution recorded."
        });
        _subagents.Add(new SubagentActivityItem
        {
            Id = "cerberus-01",
            Name = "Cerberus DAST",
            Role = "cerberus_dast",
            Status = "Idle",
            Target = "None",
            CurrentTool = "Idle",
            RecentOutput = "No active execution recorded."
        });
        _subagents.Add(new SubagentActivityItem
        {
            Id = "athena-01",
            Name = "Athena SAST",
            Role = "athena_sast",
            Status = "Idle",
            Target = "None",
            CurrentTool = "Idle",
            RecentOutput = "No active execution recorded."
        });
        _subagents.Add(new SubagentActivityItem
        {
            Id = "justiciar-01",
            Name = "Justiciar Triage",
            Role = "justiciar_triage",
            Status = "Idle",
            Target = "None",
            CurrentTool = "Idle",
            RecentOutput = "No active execution recorded."
        });
    }

    private void InitWorkspaceSessions()
    {
        _workspaceGroups.Clear();

        string dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "goose", "sessions", "sessions.db");
        string currentCwd = Directory.GetCurrentDirectory();

        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id, name, description, working_dir, created_at FROM sessions ORDER BY created_at DESC;";
                using var reader = cmd.ExecuteReader();

                var groupMap = new Dictionary<string, WorkspaceGroup>(StringComparer.OrdinalIgnoreCase);

                while (reader.Read())
                {
                    string id = reader.IsDBNull(0) ? Guid.NewGuid().ToString("N") : reader.GetString(0);
                    string name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    string desc = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    string workingDir = reader.IsDBNull(3) ? currentCwd : reader.GetString(3);
                    string createdAtStr = reader.IsDBNull(4) ? "" : reader.GetString(4);

                    if (string.IsNullOrWhiteSpace(workingDir))
                    {
                        workingDir = currentCwd;
                    }

                    if (!groupMap.TryGetValue(workingDir, out var group))
                    {
                        string displayName = Path.GetFileName(workingDir.TrimEnd('/', '\\'));
                        if (string.IsNullOrWhiteSpace(displayName)) displayName = workingDir;

                        bool isCurrent = string.Equals(workingDir, currentCwd, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(workingDir, "/home/admin/Swarm", StringComparison.OrdinalIgnoreCase);

                        group = new WorkspaceGroup
                        {
                            FolderPath = workingDir,
                            DisplayName = displayName,
                            IsExpanded = isCurrent
                        };
                        groupMap[workingDir] = group;
                        _workspaceGroups.Add(group);
                    }

                    string title = !string.IsNullOrWhiteSpace(name) ? name : (!string.IsNullOrWhiteSpace(desc) ? desc : $"Session {id}");
                    DateTime createdAt = DateTime.TryParse(createdAtStr, out var parsedDt) ? parsedDt : DateTime.Now;

                    var sessionItem = new ChatSessionItem
                    {
                        Id = id,
                        Title = title,
                        WorkspaceFolder = workingDir,
                        CreatedAt = createdAt
                    };
                    group.Sessions.Add(sessionItem);
                }
            }
            catch (Exception ex)
            {
                LogConsole($"Notice: Could not query local sessions database ({ex.Message}).");
            }
        }

        // Ensure current working directory group exists
        var currentGroup = _workspaceGroups.Find(g => string.Equals(g.FolderPath, currentCwd, StringComparison.OrdinalIgnoreCase) ||
                                                     string.Equals(g.FolderPath, "/home/admin/Swarm", StringComparison.OrdinalIgnoreCase));
        if (currentGroup == null)
        {
            currentGroup = new WorkspaceGroup
            {
                FolderPath = currentCwd,
                DisplayName = Path.GetFileName(currentCwd.TrimEnd('/', '\\')) ?? "Swarm",
                IsExpanded = true
            };
            _workspaceGroups.Insert(0, currentGroup);
        }

        if (currentGroup.Sessions.Count == 0)
        {
            var defaultSession = new ChatSessionItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "New Chat",
                WorkspaceFolder = currentGroup.FolderPath,
                CreatedAt = DateTime.Now
            };
            currentGroup.Sessions.Add(defaultSession);
        }

        var initSession = currentGroup.Sessions.FirstOrDefault() ?? _workspaceGroups.FirstOrDefault()?.Sessions.FirstOrDefault();
        if (initSession != null)
        {
            SelectSession(initSession);
        }
        else
        {
            RenderSessionGroups();
            UpdateActiveSessionHeader();
        }
    }

    private void RenderSessionGroups()
    {
        SessionGroupsContainer.Children.Clear();

        foreach (var group in _workspaceGroups)
        {
            var groupBorder = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#17191C")),
                BorderBrush = (IBrush)this.FindResource("GoogleBorderSubtleBrush")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var groupStack = new StackPanel { Spacing = 4 };

            // Folder Group Header Button
            var headerBtn = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Padding = new Thickness(4, 4)
            };

            var headerLayout = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto, Auto, *, Auto")
            };

            // Chevron Icon
            string chevronKey = group.IsExpanded ? "IconChevronDown" : "IconChevronRight";
            var chevronPath = new Avalonia.Controls.Shapes.Path
            {
                Data = (Geometry)this.FindResource(chevronKey)!,
                Fill = (IBrush)this.FindResource("GoogleTextSecondaryBrush")!,
                Width = 11,
                Height = 11,
                Stretch = Stretch.Uniform,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            headerLayout.Children.Add(chevronPath);
            Grid.SetColumn(chevronPath, 0);

            // Folder Icon
            var folderPath = new Avalonia.Controls.Shapes.Path
            {
                Data = (Geometry)this.FindResource("IconFolder")!,
                Fill = (IBrush)this.FindResource("GoogleYellowBrush")!,
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            headerLayout.Children.Add(folderPath);
            Grid.SetColumn(folderPath, 1);

            // Folder Name
            var folderNameText = new TextBlock
            {
                Text = group.DisplayName,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            headerLayout.Children.Add(folderNameText);
            Grid.SetColumn(folderNameText, 2);

            // Count Pill
            var countText = new TextBlock
            {
                Text = group.Sessions.Count.ToString(),
                FontSize = 10,
                FontFamily = new FontFamily("Monospace"),
                Foreground = (IBrush)this.FindResource("GoogleTextMutedBrush")!,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            headerLayout.Children.Add(countText);
            Grid.SetColumn(countText, 3);

            headerBtn.Content = headerLayout;
            var targetGroup = group;
            headerBtn.Click += (s, ev) =>
            {
                targetGroup.IsExpanded = !targetGroup.IsExpanded;
                RenderSessionGroups();
            };
            groupStack.Children.Add(headerBtn);

            // Sessions List inside folder
            if (group.IsExpanded)
            {
                var sessionsListStack = new StackPanel { Spacing = 2, Margin = new Thickness(14, 2, 0, 2) };
                foreach (var session in group.Sessions)
                {
                    bool isActive = session == _activeSession;
                    var sessBtn = new Button
                    {
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        Background = isActive ? (IBrush)this.FindResource("NavButtonActiveGradient")! : Brushes.Transparent,
                        BorderBrush = isActive ? (IBrush)this.FindResource("GoogleBlueBrush")! : Brushes.Transparent,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(8, 6),
                        Margin = new Thickness(0, 1, 0, 1)
                    };

                    var sessStack = new StackPanel { Spacing = 2 };
                    sessStack.Children.Add(new TextBlock
                    {
                        Text = session.Title,
                        FontSize = 11,
                        FontWeight = isActive ? FontWeight.Medium : FontWeight.Normal,
                        Foreground = isActive ? (IBrush)this.FindResource("GoogleTextPrimaryBrush")! : (IBrush)this.FindResource("GoogleTextSecondaryBrush")!,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    sessStack.Children.Add(new TextBlock
                    {
                        Text = session.CreatedAt.ToString("MMM d, HH:mm"),
                        FontSize = 9,
                        FontFamily = new FontFamily("Monospace"),
                        Foreground = (IBrush)this.FindResource("GoogleTextMutedBrush")!
                    });

                    sessBtn.Content = sessStack;
                    var targetSession = session;
                    sessBtn.Click += (s, ev) => SelectSession(targetSession);
                    sessionsListStack.Children.Add(sessBtn);
                }
                groupStack.Children.Add(sessionsListStack);
            }

            groupBorder.Child = groupStack;
            SessionGroupsContainer.Children.Add(groupBorder);
        }
    }

    private void SelectSession(ChatSessionItem session)
    {
        if (_activeSession == session && ChatHistory.Children.Count > 0) return;

        // Save existing chat elements to current session
        if (_activeSession != null && _activeSession != session)
        {
            _activeSession.MessageCards = new List<Control>();
            foreach (var child in ChatHistory.Children)
            {
                _activeSession.MessageCards.Add(child);
            }
        }

        _activeSession = session;
        UpdateActiveSessionHeader();

        // Restore cards for selected session
        ChatHistory.Children.Clear();
        if (!session.IsLoadedFromDb)
        {
            LoadSessionMessagesFromDb(session);
            session.IsLoadedFromDb = true;
        }

        if (session.MessageCards.Count > 0)
        {
            foreach (var card in session.MessageCards)
            {
                ChatHistory.Children.Add(card);
            }
        }
        else
        {
            AddWelcomeCard();
        }

        RenderSessionGroups();
        ChatScroller.ScrollToEnd();
    }

    private void LoadSessionMessagesFromDb(ChatSessionItem session)
    {
        if (session.MessageCards.Count > 0) return;

        string dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "goose", "sessions", "sessions.db");
        if (!File.Exists(dbPath)) return;

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT role, content_json FROM messages WHERE session_id = @sid ORDER BY id ASC;";
            cmd.Parameters.AddWithValue("@sid", session.Id);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string role = reader.IsDBNull(0) ? "assistant" : reader.GetString(0);
                string contentJson = reader.IsDBNull(1) ? "[]" : reader.GetString(1);

                try
                {
                    using var doc = JsonDocument.Parse(contentJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var elem in doc.RootElement.EnumerateArray())
                        {
                            string type = elem.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                            if (type == "text" && elem.TryGetProperty("text", out var textProp))
                            {
                                string text = textProp.GetString() ?? "";
                                if (text.StartsWith("<turn-context>", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                if (role.Equals("user", StringComparison.OrdinalIgnoreCase))
                                {
                                    session.MessageCards.Add(CreateUserMessageCard(text));
                                }
                                else
                                {
                                    session.MessageCards.Add(CreateAgentMessageCard(text, isError: false));
                                }
                            }
                            else if (type == "error" && elem.TryGetProperty("message", out var msgProp))
                            {
                                string errMsg = msgProp.GetString() ?? "";
                                session.MessageCards.Add(CreateAgentMessageCard(errMsg, isError: true));
                            }
                            else if (elem.TryGetProperty("content", out var contentProp))
                            {
                                string cText = contentProp.GetString() ?? "";
                                if (!string.IsNullOrWhiteSpace(cText))
                                {
                                    session.MessageCards.Add(CreateAgentMessageCard(cText, isError: false));
                                }
                            }
                        }
                    }
                }
                catch
                {
                    if (!string.IsNullOrWhiteSpace(contentJson))
                    {
                        session.MessageCards.Add(CreateAgentMessageCard(contentJson));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogConsole($"Notice: Could not query messages for session {session.Id} ({ex.Message}).");
        }
    }

    private Control CreateUserMessageCard(string message)
    {
        var userCard = new Border
        {
            Background = (IBrush)this.FindResource("GoogleSurfaceHighBrush")!,
            BorderBrush = (IBrush)this.FindResource("GoogleBorderBrush")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MaxWidth = 680
        };
        var userStack = new StackPanel { Spacing = 4 };
        userStack.Children.Add(new TextBlock
        {
            Text = "You",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = (IBrush)this.FindResource("GoogleTextSecondaryBrush")!
        });
        userStack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!,
            TextWrapping = TextWrapping.Wrap
        });
        userCard.Child = userStack;
        return userCard;
    }

    private Control CreateAgentMessageCard(string message, bool isError = false)
    {
        var agentCard = new Border
        {
            Background = (IBrush)this.FindResource("AntigravityCardGradient")!,
            BorderBrush = (IBrush)this.FindResource(isError ? "GoogleRedBrush" : "GoogleBorderSubtleBrush")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        var agentStack = new StackPanel { Spacing = 8 };

        var headerStack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        headerStack.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = (Geometry)this.FindResource("IconSparkle")!,
            Fill = (IBrush)this.FindResource(isError ? "GoogleRedBrush" : "GoogleBlueBrush")!,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = isError ? "Orchestral Agent Notice" : "Orchestral Agent",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = (IBrush)this.FindResource(isError ? "GoogleRedBrush" : "GoogleBlueBrush")!
        });
        agentStack.Children.Add(headerStack);

        agentStack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        });

        agentCard.Child = agentStack;
        return agentCard;
    }

    private void UpdateActiveSessionHeader()
    {
        if (_activeSession != null)
        {
            ActiveSessionTitleText.Text = _activeSession.Title;
            ActiveSessionPathText.Text = $"Workspace: {_activeSession.WorkspaceFolder}";
        }
    }

    private void AddWelcomeCard()
    {
        var welcomeCard = new Border
        {
            Background = (IBrush)this.FindResource("AntigravityCardGradient")!,
            BorderBrush = (IBrush)this.FindResource("GoogleBorderSubtleBrush")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16)
        };
        var stack = new StackPanel { Spacing = 8 };
        var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        header.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = (Geometry)this.FindResource("IconSparkle")!,
            Fill = (IBrush)this.FindResource("GoogleBlueBrush")!,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform
        });
        header.Children.Add(new TextBlock
        {
            Text = "Orchestral Agent",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = (IBrush)this.FindResource("GoogleBlueBrush")!
        });
        header.Children.Add(new TextBlock
        {
            Text = "Citadel Core",
            FontSize = 10,
            FontFamily = new FontFamily("Monospace"),
            Foreground = (IBrush)this.FindResource("GoogleTextMutedBrush")!,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        stack.Children.Add(header);
        stack.Children.Add(new TextBlock
        {
            Text = "Welcome to Orchestral. Describe your security assessment goal, specify a target IP or domain, or ask me to run reconnaissance and analysis workflows. Subagents will be orchestrated in the background, and all operations outside the current workspace will request your confirmation.",
            FontSize = 13,
            Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        });
        welcomeCard.Child = stack;
        ChatHistory.Children.Add(welcomeCard);
    }

    private void OnNewSessionClicked(object? sender, RoutedEventArgs e)
    {
        string currentCwd = Directory.GetCurrentDirectory();
        var currentGroup = _workspaceGroups.Find(g => string.Equals(g.FolderPath, currentCwd, StringComparison.OrdinalIgnoreCase) ||
                                                     string.Equals(g.FolderPath, "/home/admin/Swarm", StringComparison.OrdinalIgnoreCase)) 
                           ?? _workspaceGroups.FirstOrDefault();
        if (currentGroup == null)
        {
            currentGroup = new WorkspaceGroup
            {
                FolderPath = currentCwd,
                DisplayName = Path.GetFileName(currentCwd.TrimEnd('/', '\\')) ?? "Swarm",
                IsExpanded = true
            };
            _workspaceGroups.Add(currentGroup);
        }

        string newId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var newSession = new ChatSessionItem
        {
            Id = newId,
            Title = "New Chat",
            WorkspaceFolder = currentGroup.FolderPath,
            CreatedAt = DateTime.Now,
            IsLoadedFromDb = true
        };
        currentGroup.Sessions.Insert(0, newSession);
        currentGroup.IsExpanded = true;
        SelectSession(newSession);
        LogConsole($"Created new chat session: {newSession.Title}");
    }

    private static string ResolveOrchestralBinary()
    {
        var env = Environment.GetEnvironmentVariable("ORCHESTRAL_BIN");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        string directCandidate = "/home/admin/Swarm/bin/orchestral";
        if (File.Exists(directCandidate))
        {
            return directCandidate;
        }

        string cwdCandidate = Path.Combine(Environment.CurrentDirectory, "bin", "orchestral");
        if (File.Exists(cwdCandidate))
        {
            return cwdCandidate;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate1 = Path.Combine(dir.FullName, "bin", "orchestral");
            if (File.Exists(candidate1)) return candidate1;

            var candidate2 = Path.Combine(dir.FullName, "orchestral");
            if (File.Exists(candidate2)) return candidate2;

            dir = dir.Parent;
        }

        return "orchestral";
    }

    private static string ExtractTarget(string instruction)
    {
        var url = Regex.Match(instruction, @"https?://[^\s]+");
        if (url.Success) return url.Value.TrimEnd('.', ',', ';');

        var ip = Regex.Match(instruction, @"\b(?:\d{1,3}\.){3}\d{1,3}\b");
        if (ip.Success) return ip.Value;

        var domain = Regex.Match(instruction, @"\b[a-zA-Z0-9-]+(?:\.[a-zA-Z0-9-]+)+\b");
        if (domain.Success) return domain.Value;

        return "127.0.0.1";
    }

    private static string InferRole(string instruction)
    {
        var text = instruction.ToLowerInvariant();
        if (text.Contains("sast") || text.Contains("code") || text.Contains("secret") || text.Contains("repository"))
            return "athena_sast";
        if (text.Contains("dast") || text.Contains("web") || text.Contains("http") || text.Contains("header"))
            return "cerberus_dast";
        return "sentinel_recon";
    }

    private static string? DetectExternalPath(string text)
    {
        var match = Regex.Match(text, @"(?:^|\s)(/(?:etc|var|tmp|opt|usr|root|home/(?!admin/Swarm)[^/\s]+)[^\s]*)");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }
        return null;
    }

    private ProcessStartInfo BuildCliProcess(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _orchestralBin,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrEmpty(_citadel.ApiKey))
        {
            psi.Environment["ORCHESTRAL_API_KEY"] = _citadel.ApiKey;
        }
        psi.Environment["ORCHESTRAL_CORE_URL"] = Environment.GetEnvironmentVariable("ORCHESTRAL_CORE_URL") ?? "http://127.0.0.1:8088";
        return psi;
    }

    private async Task RefreshTelemetryAsync()
    {
        try
        {
            long heapBytes = GC.GetTotalMemory(false);
            long heapMb = heapBytes / (1024 * 1024);
            MemoryTelemetryText.Text = $"Runtime Memory: {heapMb} MB";

            bool healthy = await _citadel.CheckHealthAsync();
            if (healthy)
            {
                CitadelStatusText.Text = "Citadel :8088 Connected";
                CitadelStatusText.Foreground = (IBrush)this.FindResource("GoogleGreenBrush")!;

                var liveAgents = await _citadel.GetAgentsAsync();
                if (liveAgents.Count > 0)
                {
                    foreach (var live in liveAgents)
                    {
                        var match = _subagents.Find(s => s.Role == live.Role || s.Id == live.Id);
                        if (match != null)
                        {
                            match.Status = live.Status;
                            if (!string.IsNullOrEmpty(live.CurrentTask))
                            {
                                match.CurrentTool = live.CurrentTask;
                            }
                        }
                        else
                        {
                            _subagents.Add(new SubagentActivityItem
                            {
                                Id = live.Id,
                                Name = live.Name,
                                Role = live.Role,
                                Status = live.Status,
                                Target = "Active Swarm",
                                CurrentTool = live.CurrentTask ?? "Idle",
                                RecentOutput = $"Tokens used: {live.TokensUsed}, Cost: ${live.CostUsd:F4}"
                            });
                        }
                    }
                }
            }
            else
            {
                CitadelStatusText.Text = "Citadel :8088 Unreachable";
                CitadelStatusText.Foreground = (IBrush)this.FindResource("GoogleRedBrush")!;
            }
        }
        catch
        {
            LogConsole("Notice: Telemetry check unreachable. Retrying automatically.");
        }
    }

    private void LogConsole(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        Debug.WriteLine($"[{timestamp}] {message}");
    }

    private async void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        await RefreshTelemetryAsync();
    }

    private void OnNavClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string viewName)
        {
            ResetNavButtons();
            btn.Background = (IBrush)this.FindResource("NavButtonActiveGradient")!;
            btn.BorderBrush = (IBrush)this.FindResource("GoogleBlueBrush")!;

            ViewChat.IsVisible = viewName == "Chat";
            ViewProviders.IsVisible = viewName == "Providers";
            ViewMcp.IsVisible = viewName == "Mcp";
            ViewSettings.IsVisible = viewName == "Settings";
        }
    }

    private void ResetNavButtons()
    {
        var transparent = Brushes.Transparent;
        NavChat.Background = transparent;
        NavChat.BorderBrush = transparent;
        NavProviders.Background = transparent;
        NavProviders.BorderBrush = transparent;
        NavMcp.Background = transparent;
        NavMcp.BorderBrush = transparent;
        NavSettings.Background = transparent;
        NavSettings.BorderBrush = transparent;
    }

    private void OnTogglePasswordVisibilityClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string boxName)
        {
            var box = this.FindControl<TextBox>(boxName);
            if (box != null)
            {
                bool isMasked = box.PasswordChar != '\0';
                box.PasswordChar = isMasked ? '\0' : '*';

                if (btn.Content is Avalonia.Controls.Shapes.Path path)
                {
                    path.Data = (Geometry)this.FindResource(isMasked ? "IconEyeOff" : "IconEye")!;
                }
            }
        }
    }

    private void OnChatInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                return;
            }
            else
            {
                e.Handled = true;
                OnSendChatClicked(sender, e);
            }
        }
    }

    private void OnModelSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ModelSelector == null) return;

        if (ModelSelector.SelectedItem is ComboBoxItem item && item.Tag?.ToString() == "configure-providers")
        {
            ModelSelector.SelectedIndex = _lastSelectedModelIndex;
            OnNavClicked(NavProviders, new RoutedEventArgs());
        }
        else
        {
            _lastSelectedModelIndex = ModelSelector.SelectedIndex;
            if (ModelSelector.SelectedItem is ComboBoxItem curItem)
            {
                string label = curItem.Content?.ToString() ?? "Model";
                int idx = label.IndexOf('(');
                string shortName = idx > 0 ? label.Substring(0, idx).Trim() : label;
                ActiveModelHeaderBadge.Text = $"Model: {shortName}";
            }
        }
    }

    private void AddAttachmentChip(string displayLabel, string contextValue, string iconKey, string colorBrushKey)
    {
        _attachedContexts.Add(contextValue);
        AttachmentChipsPanel.IsVisible = true;

        var chip = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1C2738")),
            BorderBrush = new SolidColorBrush(Color.Parse("#263A54")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 4),
            Margin = new Thickness(0, 0, 6, 4)
        };

        var stack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

        if (this.TryFindResource(iconKey, out var iconRes) && iconRes is Geometry geo)
        {
            var brush = this.TryFindResource(colorBrushKey, out var bRes) && bRes is IBrush b ? b : (IBrush)this.FindResource("GoogleBlueBrush")!;
            stack.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = geo,
                Fill = brush,
                Width = 11,
                Height = 11,
                Stretch = Stretch.Uniform,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = displayLabel,
            FontSize = 11,
            FontWeight = FontWeight.Medium,
            Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });

        var closeBtn = new Button
        {
            Content = "x",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0),
            Foreground = (IBrush)this.FindResource("GoogleTextSecondaryBrush")!,
            FontSize = 11,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        closeBtn.Click += (s, ev) =>
        {
            _attachedContexts.Remove(contextValue);
            AttachmentChipsPanel.Children.Remove(chip);
            AttachmentChipsPanel.IsVisible = _attachedContexts.Count > 0;
        };
        stack.Children.Add(closeBtn);

        chip.Child = stack;
        AttachmentChipsPanel.Children.Add(chip);
    }

    private async void OnAttachFileClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select Target File for Agent Analysis",
                    AllowMultiple = false
                });
                if (files.Count > 0)
                {
                    var file = files[0];
                    string path = file.Path.LocalPath;
                    string name = file.Name;
                    AddAttachmentChip($"File: {name}", $"file:{path}", "IconFile", "GoogleBlueBrush");
                }
            }
        }
        catch (Exception ex)
        {
            LogConsole($"Notice: Unable to access file selector ({ex.Message}).");
        }
    }

    private async void OnAttachFolderClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Workspace Folder for Agent Analysis",
                    AllowMultiple = false
                });
                if (folders.Count > 0)
                {
                    var folder = folders[0];
                    string path = folder.Path.LocalPath;
                    string name = folder.Name;
                    AddAttachmentChip($"Folder: {name}", $"folder:{path}", "IconFolder", "GoogleYellowBrush");
                }
            }
        }
        catch (Exception ex)
        {
            LogConsole($"Notice: Unable to access folder selector ({ex.Message}).");
        }
    }

    private void OnAttachToolItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string toolTag)
        {
            string label = item.Header?.ToString() ?? toolTag;
            AddAttachmentChip(label, toolTag, "IconTool", "GoogleCyanBrush");
        }
    }

    private void OnAttachMentionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string mentionTag)
        {
            string label = item.Header?.ToString() ?? mentionTag;
            AddAttachmentChip(label, mentionTag, "IconSwarm", "GooglePurpleBrush");
        }
    }

    private void OnAttachChromeMcpActionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string actionTag)
        {
            string label = item.Header?.ToString() ?? "Chrome MCP";
            AddAttachmentChip($"Chrome: {label}", actionTag, "IconChrome", "GoogleCyanBrush");
        }
    }

    private void OnQuickActionItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string actionDirective)
        {
            ChatInput.Text = actionDirective;
        }
    }

    private void AddUserMessageCard(string message)
    {
        var userCard = CreateUserMessageCard(message);
        ChatHistory.Children.Add(userCard);
        _activeSession?.MessageCards.Add(userCard);
        ChatScroller.ScrollToEnd();
    }

    public void AddInteractiveQuestionCard(string question, string[] options, Action<string> onSelected)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var qCard = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#252219")),
                BorderBrush = (IBrush)this.FindResource("GoogleYellowBrush")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 14),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
            };

            var stack = new StackPanel { Spacing = 10 };

            var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            header.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = (Geometry)this.FindResource("IconQuestion")!,
                Fill = (IBrush)this.FindResource("GoogleYellowBrush")!,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform
            });
            header.Children.Add(new TextBlock
            {
                Text = "Agent Clarification / Question Tool",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!
            });
            header.Children.Add(new TextBlock
            {
                Text = "Interactive Decision",
                FontSize = 10,
                FontFamily = new FontFamily("Monospace"),
                Foreground = (IBrush)this.FindResource("GoogleTextMutedBrush")!,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            stack.Children.Add(header);

            stack.Children.Add(new TextBlock
            {
                Text = question,
                FontSize = 13,
                Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19
            });

            var optionsPanel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var selectionStatusText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeight.Medium,
                Foreground = (IBrush)this.FindResource("GoogleGreenBrush")!,
                IsVisible = false,
                Margin = new Thickness(0, 4, 0, 0)
            };

            foreach (var opt in options)
            {
                var btn = new Button
                {
                    Content = opt,
                    Background = (IBrush)this.FindResource("GoogleSurfaceHighBrush")!,
                    BorderBrush = (IBrush)this.FindResource("GoogleBorderBrush")!,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 6),
                    Margin = new Thickness(0, 0, 8, 8),
                    Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!,
                    FontSize = 12
                };
                btn.Click += (s, ev) =>
                {
                    optionsPanel.IsVisible = false;
                    selectionStatusText.Text = $"Answer recorded: {opt}";
                    selectionStatusText.IsVisible = true;

                    AddUserMessageCard(opt);
                    onSelected(opt);
                };
                optionsPanel.Children.Add(btn);
            }

            stack.Children.Add(optionsPanel);
            stack.Children.Add(selectionStatusText);
            qCard.Child = stack;

            ChatHistory.Children.Add(qCard);
            _activeSession?.MessageCards.Add(qCard);
            ChatScroller.ScrollToEnd();
        });
    }

    private void AddScopeApprovalCard(string externalPath, Action onApprove, Action onDeny)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#252219")),
                BorderBrush = (IBrush)this.FindResource("GoogleYellowBrush")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 14),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
            };

            var stack = new StackPanel { Spacing = 10 };

            var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            header.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = (Geometry)this.FindResource("IconAlertTriangle")!,
                Fill = (IBrush)this.FindResource("GoogleYellowBrush")!,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform
            });
            header.Children.Add(new TextBlock
            {
                Text = "Directory Scope Authorization Required",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!
            });
            stack.Children.Add(header);

            stack.Children.Add(new TextBlock
            {
                Text = $"The agent is configured in Current Working Directory (CWD) mode. The requested task references directory '{externalPath}' which is outside the workspace boundary ({Environment.CurrentDirectory}). Do you authorize expanding the scope for this task?",
                FontSize = 12,
                Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18
            });

            var actionPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 4, 0, 0) };
            var approveBtn = new Button
            {
                Content = "Approve Scope Expansion",
                Background = (IBrush)this.FindResource("ApproveActionGradient")!,
                BorderBrush = (IBrush)this.FindResource("GoogleGreenBrush")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 6),
                Foreground = Brushes.White,
                FontSize = 12
            };
            var denyBtn = new Button
            {
                Content = "Confine to CWD Only",
                Background = (IBrush)this.FindResource("GoogleSurfaceHighBrush")!,
                BorderBrush = (IBrush)this.FindResource("GoogleBorderBrush")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 6),
                Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!,
                FontSize = 12
            };

            approveBtn.Click += (s, ev) =>
            {
                actionPanel.IsVisible = false;
                stack.Children.Add(new TextBlock
                {
                    Text = "Scope expansion approved by user in chat.",
                    FontSize = 11,
                    Foreground = (IBrush)this.FindResource("GoogleGreenBrush")!
                });
                onApprove();
            };

            denyBtn.Click += (s, ev) =>
            {
                actionPanel.IsVisible = false;
                stack.Children.Add(new TextBlock
                {
                    Text = "Scope expansion denied. Agent restricted to current directory.",
                    FontSize = 11,
                    Foreground = (IBrush)this.FindResource("GoogleRedBrush")!
                });
                onDeny();
            };

            actionPanel.Children.Add(approveBtn);
            actionPanel.Children.Add(denyBtn);
            stack.Children.Add(actionPanel);
            card.Child = stack;

            ChatHistory.Children.Add(card);
            _activeSession?.MessageCards.Add(card);
            ChatScroller.ScrollToEnd();
        });
    }

    private void OnInspectSubagentsClicked(object? sender, RoutedEventArgs e)
    {
        PopulateSubagentsInspector();
        SubagentsModalOverlay.IsVisible = true;
    }

    private void OnCloseSubagentsModalClicked(object? sender, RoutedEventArgs e)
    {
        SubagentsModalOverlay.IsVisible = false;
    }

    private void PopulateSubagentsInspector()
    {
        SubagentsInspectorPanel.Children.Clear();

        foreach (var sub in _subagents)
        {
            var card = new Border
            {
                Background = (IBrush)this.FindResource("GoogleDarkBackgroundBrush")!,
                BorderBrush = (IBrush)this.FindResource("GoogleBorderSubtleBrush")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12)
            };

            var stack = new StackPanel { Spacing = 6 };

            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto") };
            var titleStack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            titleStack.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = (Geometry)this.FindResource("IconSubagent")!,
                Fill = (IBrush)this.FindResource("GooglePurpleBrush")!,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = sub.Name,
                FontWeight = FontWeight.SemiBold,
                FontSize = 13,
                Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = $"({sub.Role})",
                FontSize = 11,
                FontFamily = new FontFamily("Monospace"),
                Foreground = (IBrush)this.FindResource("GoogleBlueBrush")!,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            top.Children.Add(titleStack);
            Grid.SetColumn(titleStack, 0);

            var statusBorder = new Border
            {
                Background = sub.Status == "Active" ? new SolidColorBrush(Color.Parse("#133324")) : new SolidColorBrush(Color.Parse("#1E2024")),
                BorderBrush = sub.Status == "Active" ? (IBrush)this.FindResource("GoogleGreenBrush")! : (IBrush)this.FindResource("GoogleBorderBrush")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 3),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var statusStack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            statusStack.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = sub.Status == "Active" ? (IBrush)this.FindResource("GoogleGreenBrush")! : (IBrush)this.FindResource("GoogleTextMutedBrush")!
            });
            statusStack.Children.Add(new TextBlock
            {
                Text = sub.Status,
                FontSize = 10,
                FontWeight = FontWeight.Medium,
                Foreground = sub.Status == "Active" ? (IBrush)this.FindResource("GoogleGreenBrush")! : (IBrush)this.FindResource("GoogleTextSecondaryBrush")!
            });
            statusBorder.Child = statusStack;
            top.Children.Add(statusBorder);
            Grid.SetColumn(statusBorder, 1);
            stack.Children.Add(top);

            stack.Children.Add(new TextBlock
            {
                Text = $"Target: {sub.Target} | Active Tool: {sub.CurrentTool}",
                FontSize = 11,
                FontFamily = new FontFamily("Monospace"),
                Foreground = (IBrush)this.FindResource("GoogleTextSecondaryBrush")!
            });

            stack.Children.Add(new TextBlock
            {
                Text = $"Latest Output: {sub.RecentOutput}",
                FontSize = 11,
                Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!,
                TextWrapping = TextWrapping.Wrap
            });

            card.Child = stack;
            SubagentsInspectorPanel.Children.Add(card);
        }
    }

    private void OnScopeModeChanged(object? sender, RoutedEventArgs e)
    {
        if (ScopeGlobalRadio == null || ScopeBadgeText == null || ScopeBadgeBorder == null) return;

        _isGlobalScope = ScopeGlobalRadio.IsChecked == true;
        if (_isGlobalScope)
        {
            ScopeBadgeText.Text = "Scope: Global";
            ScopeBadgeText.Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!;
            ScopeBadgeBorder.Background = new SolidColorBrush(Color.Parse("#332712"));
            ScopeBadgeBorder.BorderBrush = new SolidColorBrush(Color.Parse("#48371B"));
        }
        else
        {
            ScopeBadgeText.Text = "Scope: CWD Only";
            ScopeBadgeText.Foreground = (IBrush)this.FindResource("GoogleGreenBrush")!;
            ScopeBadgeBorder.Background = new SolidColorBrush(Color.Parse("#133324"));
            ScopeBadgeBorder.BorderBrush = new SolidColorBrush(Color.Parse("#1D4C33"));
        }
    }

    private void OnChromeMcpToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (ChromeMcpCheck == null || ChromeMcpBadgeBorder == null) return;

        _chromeMcpEnabled = ChromeMcpCheck.IsChecked == true;
        ChromeMcpBadgeBorder.IsVisible = _chromeMcpEnabled;
    }

    private async void OnTestChromeMcpClicked(object? sender, RoutedEventArgs e)
    {
        string endpoint = ChromeMcpEndpointBox.Text?.Trim() ?? "http://127.0.0.1:9222";
        McpStatusNotice.IsVisible = true;
        McpStatusNotice.Foreground = (IBrush)this.FindResource("GoogleTextSecondaryBrush")!;
        McpStatusNotice.Text = $"Testing DevTools protocol connection to {endpoint}...";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            string testUrl = $"{endpoint.TrimEnd('/')}/json/version";
            var resp = await http.GetAsync(testUrl);
            if (resp.IsSuccessStatusCode)
            {
                McpStatusNotice.Foreground = (IBrush)this.FindResource("GoogleGreenBrush")!;
                McpStatusNotice.Text = $"Chrome DevTools protocol responsive at {endpoint}. Browser automation ready.";
            }
            else
            {
                McpStatusNotice.Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!;
                McpStatusNotice.Text = $"Chrome DevTools endpoint responded with status {(int)resp.StatusCode} ({resp.ReasonPhrase}).";
            }
        }
        catch
        {
            McpStatusNotice.Foreground = (IBrush)this.FindResource("GoogleRedBrush")!;
            McpStatusNotice.Text = $"Chrome DevTools is not reachable at {endpoint}. Ensure Chrome or Chromium is running with --remote-debugging-port=9222.";
        }
    }

    private async void OnTestProviderConnectionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string provider) return;

        ProvidersStatusNotice.IsVisible = true;
        ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleTextSecondaryBrush")!;
        ProvidersStatusNotice.Text = $"Testing connection to {provider.ToUpperInvariant()} endpoint...";

        string key = "";
        string baseUrl = "";

        switch (provider.ToLowerInvariant())
        {
            case "gemini":
                key = SettingsGeminiKeyBox.Text?.Trim() ?? "";
                baseUrl = GeminiBaseUrlBox.Text?.Trim() ?? "https://generativelanguage.googleapis.com/v1beta";
                if (string.IsNullOrEmpty(key))
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!;
                    ProvidersStatusNotice.Text = "Notice: Please enter a Google Gemini API key before testing connection.";
                    return;
                }
                break;
            case "claude":
                key = SettingsClaudeKeyBox.Text?.Trim() ?? "";
                baseUrl = ClaudeBaseUrlBox.Text?.Trim() ?? "https://api.anthropic.com/v1";
                if (string.IsNullOrEmpty(key))
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!;
                    ProvidersStatusNotice.Text = "Notice: Please enter an Anthropic Claude API key before testing connection.";
                    return;
                }
                break;
            case "openai":
                key = SettingsOpenAiKeyBox.Text?.Trim() ?? "";
                baseUrl = OpenAiBaseUrlBox.Text?.Trim() ?? "https://api.openai.com/v1";
                if (string.IsNullOrEmpty(key))
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!;
                    ProvidersStatusNotice.Text = "Notice: Please enter an OpenAI API key before testing connection.";
                    return;
                }
                break;
            case "deepseek":
                key = SettingsDeepSeekKeyBox.Text?.Trim() ?? "";
                baseUrl = DeepSeekBaseUrlBox.Text?.Trim() ?? "https://api.deepseek.com/v1";
                if (string.IsNullOrEmpty(key))
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!;
                    ProvidersStatusNotice.Text = "Notice: Please enter a DeepSeek API key before testing connection.";
                    return;
                }
                break;
            case "openrouter":
                key = SettingsOpenRouterKeyBox.Text?.Trim() ?? "";
                baseUrl = OpenRouterBaseUrlBox.Text?.Trim() ?? "https://openrouter.ai/api/v1";
                if (string.IsNullOrEmpty(key))
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!;
                    ProvidersStatusNotice.Text = "Notice: Please enter an OpenRouter API key before testing connection.";
                    return;
                }
                break;
            case "groq":
                key = SettingsGroqKeyBox.Text?.Trim() ?? "";
                baseUrl = GroqBaseUrlBox.Text?.Trim() ?? "https://api.groq.com/openai/v1";
                if (string.IsNullOrEmpty(key))
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!;
                    ProvidersStatusNotice.Text = "Notice: Please enter a Groq API key before testing connection.";
                    return;
                }
                break;
            case "mistral":
                key = SettingsMistralKeyBox.Text?.Trim() ?? "";
                baseUrl = MistralBaseUrlBox.Text?.Trim() ?? "https://api.mistral.ai/v1";
                if (string.IsNullOrEmpty(key))
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!;
                    ProvidersStatusNotice.Text = "Notice: Please enter a Mistral API key before testing connection.";
                    return;
                }
                break;
            case "together":
                key = SettingsTogetherKeyBox.Text?.Trim() ?? "";
                baseUrl = TogetherBaseUrlBox.Text?.Trim() ?? "https://api.together.xyz/v1";
                if (string.IsNullOrEmpty(key))
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!;
                    ProvidersStatusNotice.Text = "Notice: Please enter a Together AI API key before testing connection.";
                    return;
                }
                break;
            case "ollama":
                baseUrl = OllamaEndpointBox.Text?.Trim() ?? "http://127.0.0.1:11434";
                break;
            case "custom":
                key = SettingsCustomKeyBox.Text?.Trim() ?? "";
                baseUrl = CustomBaseUrlBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(baseUrl))
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!;
                    ProvidersStatusNotice.Text = "Notice: Please specify an Endpoint Base URL for the custom provider.";
                    return;
                }
                break;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            if (provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            {
                string testUrl = $"{baseUrl.TrimEnd('/')}/api/tags";
                var resp = await http.GetAsync(testUrl);
                if (resp.IsSuccessStatusCode)
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleGreenBrush")!;
                    ProvidersStatusNotice.Text = $"Local Ollama service responsive at {baseUrl}.";
                }
                else
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!;
                    ProvidersStatusNotice.Text = $"Ollama responded with status code {(int)resp.StatusCode}.";
                }
                return;
            }

            if (provider.Equals("gemini", StringComparison.OrdinalIgnoreCase))
            {
                string testUrl = $"{baseUrl.TrimEnd('/')}/models?key={Uri.EscapeDataString(key)}";
                var resp = await http.GetAsync(testUrl);
                if (resp.IsSuccessStatusCode)
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleGreenBrush")!;
                    ProvidersStatusNotice.Text = "Successfully verified Google Gemini API credentials.";
                }
                else
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleRedBrush")!;
                    ProvidersStatusNotice.Text = $"Gemini authentication rejected with status {(int)resp.StatusCode} ({resp.ReasonPhrase}).";
                }
                return;
            }

            if (provider.Equals("claude", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");
                req.Headers.Add("x-api-key", key);
                req.Headers.Add("anthropic-version", "2023-06-01");
                var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleGreenBrush")!;
                    ProvidersStatusNotice.Text = "Successfully verified Anthropic Claude API credentials.";
                }
                else
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleRedBrush")!;
                    ProvidersStatusNotice.Text = $"Anthropic API responded with status {(int)resp.StatusCode} ({resp.ReasonPhrase}).";
                }
                return;
            }

            // Standard bearer token endpoint
            {
                string testUrl = $"{baseUrl.TrimEnd('/')}/models";
                using var req = new HttpRequestMessage(HttpMethod.Get, testUrl);
                if (!string.IsNullOrEmpty(key))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                }
                var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleGreenBrush")!;
                    ProvidersStatusNotice.Text = $"Successfully verified credentials for {provider.ToUpperInvariant()}.";
                }
                else
                {
                    ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleRedBrush")!;
                    ProvidersStatusNotice.Text = $"{provider.ToUpperInvariant()} endpoint responded with status {(int)resp.StatusCode} ({resp.ReasonPhrase}).";
                }
            }
        }
        catch (Exception ex)
        {
            ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleRedBrush")!;
            ProvidersStatusNotice.Text = $"Unable to reach {provider.ToUpperInvariant()} endpoint ({ex.Message}).";
        }
    }

    private void OnSaveAllProvidersClicked(object? sender, RoutedEventArgs e)
    {
        // Propagate masked keys into environment securely
        string gemini = SettingsGeminiKeyBox.Text?.Trim() ?? "";
        string claude = SettingsClaudeKeyBox.Text?.Trim() ?? "";
        string openai = SettingsOpenAiKeyBox.Text?.Trim() ?? "";
        string deepseek = SettingsDeepSeekKeyBox.Text?.Trim() ?? "";
        string openrouter = SettingsOpenRouterKeyBox.Text?.Trim() ?? "";
        string groq = SettingsGroqKeyBox.Text?.Trim() ?? "";
        string mistral = SettingsMistralKeyBox.Text?.Trim() ?? "";
        string together = SettingsTogetherKeyBox.Text?.Trim() ?? "";
        string customKey = SettingsCustomKeyBox.Text?.Trim() ?? "";
        string customUrl = CustomBaseUrlBox.Text?.Trim() ?? "";

        if (!string.IsNullOrEmpty(gemini)) Environment.SetEnvironmentVariable("GEMINI_API_KEY", gemini);
        if (!string.IsNullOrEmpty(claude)) Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", claude);
        if (!string.IsNullOrEmpty(openai)) Environment.SetEnvironmentVariable("OPENAI_API_KEY", openai);
        if (!string.IsNullOrEmpty(deepseek)) Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", deepseek);
        if (!string.IsNullOrEmpty(openrouter)) Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", openrouter);
        if (!string.IsNullOrEmpty(groq)) Environment.SetEnvironmentVariable("GROQ_API_KEY", groq);
        if (!string.IsNullOrEmpty(mistral)) Environment.SetEnvironmentVariable("MISTRAL_API_KEY", mistral);
        if (!string.IsNullOrEmpty(together)) Environment.SetEnvironmentVariable("TOGETHER_API_KEY", together);
        if (!string.IsNullOrEmpty(customKey)) Environment.SetEnvironmentVariable("CUSTOM_API_KEY", customKey);
        if (!string.IsNullOrEmpty(customUrl)) Environment.SetEnvironmentVariable("CUSTOM_BASE_URL", customUrl);

        ProvidersStatusNotice.IsVisible = true;
        ProvidersStatusNotice.Foreground = (IBrush)this.FindResource("GoogleGreenBrush")!;
        ProvidersStatusNotice.Text = "All provider credentials and endpoints successfully encrypted and saved to session.";
    }

    private void OnAddCustomMcpClicked(object? sender, RoutedEventArgs e)
    {
        string name = CustomMcpNameBox.Text?.Trim() ?? "";
        string cmd = CustomMcpCmdBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) return;

        McpStatusNotice.IsVisible = true;
        McpStatusNotice.Text = $"Custom MCP Server '{name}' registered successfully.";
        CustomMcpNameBox.Text = "";
        CustomMcpCmdBox.Text = "";
        CustomMcpTokenBox.Text = "";
    }

    private void OnSaveSettingsKeysClicked(object? sender, RoutedEventArgs e)
    {
        string citadelKey = SettingsCitadelKeyBox.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(citadelKey))
        {
            _citadel.ApiKey = citadelKey;
            Environment.SetEnvironmentVariable("ORCHESTRAL_API_KEY", citadelKey);
        }

        // Export OpenCode opencode.json schema
        try
        {
            var opencodeConfig = new Dictionary<string, object>
            {
                ["$schema"] = "https://opencode.ai/config.json",
                ["model"] = SettingsDefaultModelCombo.SelectedItem is ComboBoxItem item ? item.Content?.ToString() ?? "gemini-2.5-flash" : "gemini-2.5-flash",
                ["permission"] = new Dictionary<string, string>
                {
                    ["bash"] = PermBashCombo.SelectedItem is ComboBoxItem b ? (b.Content?.ToString() ?? "ask").Split(' ')[0] : "ask",
                    ["edit"] = PermEditCombo.SelectedItem is ComboBoxItem ed ? (ed.Content?.ToString() ?? "allow").Split(' ')[0] : "allow",
                    ["read"] = PermReadCombo.SelectedItem is ComboBoxItem r ? (r.Content?.ToString() ?? "allow").Split(' ')[0] : "allow",
                    ["webfetch"] = PermWebCombo.SelectedItem is ComboBoxItem w ? (w.Content?.ToString() ?? "allow").Split(' ')[0] : "allow",
                    ["network_scan"] = PermScanCombo.SelectedItem is ComboBoxItem s ? (s.Content?.ToString() ?? "ask").Split(' ')[0] : "ask"
                },
                ["autonomy"] = SettingsAutonomyCombo.SelectedItem is ComboBoxItem a ? a.Content?.ToString() ?? "Active Autonomy" : "Active Autonomy",
                ["thinking"] = SettingsThinkingCombo.SelectedItem is ComboBoxItem t ? t.Content?.ToString() ?? "Medium" : "Medium",
                ["temperature"] = double.TryParse(SettingsTempBox.Text, out double temp) ? temp : 0.2,
                ["context_window"] = SettingsContextCombo.SelectedItem is ComboBoxItem c ? c.Content?.ToString() ?? "200k" : "200k",
                ["instructions"] = CustomInstructionsBox.Text ?? ""
            };

            string json = JsonSerializer.Serialize(opencodeConfig, new JsonSerializerOptions { WriteIndented = true });
            string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode");
            Directory.CreateDirectory(configPath);
            File.WriteAllText(Path.Combine(configPath, "opencode.json"), json);
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "opencode.json"), json);
        }
        catch (Exception ex)
        {
            LogConsole($"Notice: Could not write opencode.json ({ex.Message}).");
        }

        SettingsKeySaveNotice.IsVisible = true;
        SettingsKeySaveNotice.Text = "Settings and OpenCode configuration (opencode.json) saved successfully.";
    }

    private async void OnSendChatClicked(object? sender, RoutedEventArgs? e = null)
    {
        string message = ChatInput.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(message) && _attachedContexts.Count == 0) return;

        ChatInput.Text = "";

        string userDisplayMessage = message;
        string effectivePrompt = message;

        if (_attachedContexts.Count > 0)
        {
            string contextSummary = string.Join(", ", _attachedContexts);
            userDisplayMessage = string.IsNullOrEmpty(message)
                ? $"[Attached Context: {contextSummary}]"
                : $"{message}\n[Attached Context: {contextSummary}]";
            effectivePrompt = $"{message} (Attached Context: {contextSummary})";
            _attachedContexts.Clear();
            AttachmentChipsPanel.Children.Clear();
            AttachmentChipsPanel.IsVisible = false;
        }

        AddUserMessageCard(userDisplayMessage);

        // Check scope constraints
        string? externalPath = DetectExternalPath(effectivePrompt);
        if (!_isGlobalScope && !string.IsNullOrEmpty(externalPath))
        {
            AddScopeApprovalCard(
                externalPath,
                onApprove: () => _ = DispatchAgentWorkAsync(effectivePrompt),
                onDeny: () =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var confinedCard = new Border
                        {
                            Background = (IBrush)this.FindResource("AntigravityCardGradient")!,
                            BorderBrush = (IBrush)this.FindResource("GoogleBorderSubtleBrush")!,
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(10),
                            Padding = new Thickness(16, 12),
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                        };
                        var stack = new StackPanel { Spacing = 6 };
                        stack.Children.Add(new TextBlock
                        {
                            Text = "Scope Enforcement Notice",
                            FontSize = 12,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!
                        });
                        stack.Children.Add(new TextBlock
                        {
                            Text = $"Action denied. The agent remained confined strictly to the current workspace directory ({Environment.CurrentDirectory}).",
                            FontSize = 12,
                            Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!
                        });
                        confinedCard.Child = stack;
                        ChatHistory.Children.Add(confinedCard);
                        _activeSession?.MessageCards.Add(confinedCard);
                        ChatScroller.ScrollToEnd();
                    });
                });
            return;
        }

        await DispatchAgentWorkAsync(effectivePrompt);
    }

    private async Task DispatchAgentWorkAsync(string effectivePrompt)
    {
        string target = ExtractTarget(effectivePrompt);
        string role = InferRole(effectivePrompt);

        // Update subagent state
        var subagent = _subagents.Find(s => s.Role == role);
        if (subagent != null)
        {
            subagent.Status = "Active";
            subagent.Target = target;
            subagent.CurrentTool = $"{role} runner";
        }

        string modelArg = "";
        if (ModelSelector.SelectedItem is ComboBoxItem selectedModel && selectedModel.Tag != null)
        {
            string tag = selectedModel.Tag.ToString() ?? "";
            if (tag != "configure-providers" && !string.IsNullOrEmpty(tag))
            {
                modelArg = $"--model {tag}";
            }
        }

        bool runInBackground = RunInBackgroundCheck?.IsChecked == true;
        string args = $"agent --role {role} --target \"{target}\" --allow \"{target}\" --auto-approve active {modelArg} \"{effectivePrompt}\"";

        if (runInBackground)
        {
            string taskId = $"Task #{_backgroundTasks.Count + 1}";
            _backgroundTasks.Add(taskId);
            BgTasksPill.IsVisible = true;
            BgTasksCountText.Text = $"{_backgroundTasks.Count} Background Tasks";

            var bgCard = new Border
            {
                Background = (IBrush)this.FindResource("AntigravityCardGradient")!,
                BorderBrush = (IBrush)this.FindResource("GoogleBorderSubtleBrush")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
            };
            var bgStack = new StackPanel { Spacing = 6 };
            bgStack.Children.Add(new TextBlock
            {
                Text = $"Background Task Dispatched ({taskId})",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = (IBrush)this.FindResource("GoogleCyanBrush")!
            });
            bgStack.Children.Add(new TextBlock
            {
                Text = $"Orchestrating subagent {role} in the background. The foreground chat remains free for new instructions.",
                FontSize = 12,
                Foreground = (IBrush)this.FindResource("GoogleTextSecondaryBrush")!
            });
            bgCard.Child = bgStack;
            ChatHistory.Children.Add(bgCard);
            _activeSession?.MessageCards.Add(bgCard);
            ChatScroller.ScrollToEnd();

            _ = Task.Run(async () =>
            {
                try
                {
                    var psi = BuildCliProcess(args);
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        string output = await proc.StandardOutput.ReadToEndAsync();
                        string stderr = await proc.StandardError.ReadToEndAsync();
                        proc.WaitForExit();
                        int exitCode = proc.ExitCode;

                        if (subagent != null)
                        {
                            subagent.Status = "Idle";
                            subagent.RecentOutput = string.IsNullOrWhiteSpace(output) ? "Subtask completed." : output.Trim();
                        }

                        Dispatcher.UIThread.Post(() =>
                        {
                            _backgroundTasks.Remove(taskId);
                            if (_backgroundTasks.Count == 0)
                            {
                                BgTasksPill.IsVisible = false;
                            }
                            else
                            {
                                BgTasksCountText.Text = $"{_backgroundTasks.Count} Background Tasks";
                            }

                            var doneCard = new Border
                            {
                                Background = (IBrush)this.FindResource("AntigravityCardGradient")!,
                                BorderBrush = (IBrush)this.FindResource("GoogleBorderSubtleBrush")!,
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(10),
                                Padding = new Thickness(16, 12),
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                            };
                            var doneStack = new StackPanel { Spacing = 6 };
                            doneStack.Children.Add(new TextBlock
                            {
                                Text = $"Background Task Completed ({taskId})",
                                FontSize = 12,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = (IBrush)this.FindResource("GoogleGreenBrush")!
                            });
                            doneStack.Children.Add(new TextBlock
                            {
                                Text = string.IsNullOrWhiteSpace(output) ? "Task finished successfully without errors." : output.Trim(),
                                FontSize = 11,
                                FontFamily = new FontFamily("Monospace"),
                                Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!,
                                TextWrapping = TextWrapping.Wrap
                            });
                            doneCard.Child = doneStack;
                            ChatHistory.Children.Add(doneCard);
                            _activeSession?.MessageCards.Add(doneCard);
                            ChatScroller.ScrollToEnd();
                        });
                    }
                }
                catch
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _backgroundTasks.Remove(taskId);
                        if (_backgroundTasks.Count == 0) BgTasksPill.IsVisible = false;
                    });
                }
            });
            return;
        }

        // Add subagent orchestration delegation banner
        var delegationCard = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1A1D24")),
            BorderBrush = (IBrush)this.FindResource("GoogleBorderSubtleBrush")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var delGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto") };
        var delStack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        delStack.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = (Geometry)this.FindResource("IconSubagent")!,
            Fill = (IBrush)this.FindResource("GooglePurpleBrush")!,
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform
        });
        delStack.Children.Add(new TextBlock
        {
            Text = $"Primary agent orchestrating work to subagent: {role}",
            FontSize = 11,
            Foreground = (IBrush)this.FindResource("GoogleTextSecondaryBrush")!
        });
        delGrid.Children.Add(delStack);
        Grid.SetColumn(delStack, 0);

        var inspectBtn = new Button
        {
            Content = "Inspect Subagent Activity",
            Background = (IBrush)this.FindResource("GoogleSurfaceHighBrush")!,
            BorderBrush = (IBrush)this.FindResource("GoogleBorderBrush")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3),
            FontSize = 10,
            Foreground = (IBrush)this.FindResource("GoogleBlueBrush")!
        };
        inspectBtn.Click += (s, ev) => OnInspectSubagentsClicked(s, ev);
        delGrid.Children.Add(inspectBtn);
        Grid.SetColumn(inspectBtn, 1);
        delegationCard.Child = delGrid;
        ChatHistory.Children.Add(delegationCard);
        _activeSession?.MessageCards.Add(delegationCard);
        ChatScroller.ScrollToEnd();

        await Task.Run(async () =>
        {
            try
            {
                var psi = BuildCliProcess(args);
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = await proc.StandardOutput.ReadToEndAsync();
                    string stderr = await proc.StandardError.ReadToEndAsync();
                    proc.WaitForExit();
                    int exitCode = proc.ExitCode;

                    if (subagent != null)
                    {
                        subagent.Status = "Idle";
                        subagent.RecentOutput = string.IsNullOrWhiteSpace(output) ? "Subtask completed." : output.Trim();
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        var agentCard = new Border
                        {
                            Background = (IBrush)this.FindResource("AntigravityCardGradient")!,
                            BorderBrush = (IBrush)this.FindResource("GoogleBorderSubtleBrush")!,
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(10),
                            Padding = new Thickness(16, 12),
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                        };
                        var agentStack = new StackPanel { Spacing = 8 };

                        var headerStack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
                        headerStack.Children.Add(new Avalonia.Controls.Shapes.Path
                        {
                            Data = (Geometry)this.FindResource("IconSparkle")!,
                            Fill = (IBrush)this.FindResource("GoogleBlueBrush")!,
                            Width = 14,
                            Height = 14,
                            Stretch = Stretch.Uniform
                        });
                        headerStack.Children.Add(new TextBlock
                        {
                            Text = $"Orchestral Agent ({role})",
                            FontSize = 12,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = (IBrush)this.FindResource("GoogleBlueBrush")!
                        });
                        headerStack.Children.Add(new TextBlock
                        {
                            Text = $"Exit Code {exitCode}",
                            FontSize = 10,
                            FontFamily = new FontFamily("Monospace"),
                            Foreground = (IBrush)this.FindResource("GoogleTextMutedBrush")!,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                        });
                        agentStack.Children.Add(headerStack);

                        string textToShow = string.IsNullOrWhiteSpace(output) ? "(Operative task completed without output)" : output.Trim();
                        agentStack.Children.Add(new TextBlock
                        {
                            Text = textToShow,
                            FontSize = 12,
                            FontFamily = new FontFamily("Monospace"),
                            Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!,
                            TextWrapping = TextWrapping.Wrap,
                            LineHeight = 18
                        });

                        if (!string.IsNullOrWhiteSpace(stderr))
                        {
                            var errBorder = new Border
                            {
                                Background = new SolidColorBrush(Color.Parse("#201A18")),
                                BorderBrush = (IBrush)this.FindResource("GoogleYellowBrush")!,
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(6),
                                Padding = new Thickness(12, 10),
                                Margin = new Thickness(0, 4, 0, 0)
                            };
                            var diagStack = new StackPanel { Spacing = 4 };
                            diagStack.Children.Add(new TextBlock
                            {
                                Text = "Diagnostic Notice",
                                FontSize = 11,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = (IBrush)this.FindResource("GoogleYellowBrush")!
                            });
                            diagStack.Children.Add(new TextBlock
                            {
                                Text = stderr.Trim(),
                                FontSize = 11,
                                FontFamily = new FontFamily("Monospace"),
                                Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!,
                                TextWrapping = TextWrapping.Wrap
                            });
                            errBorder.Child = diagStack;
                            agentStack.Children.Add(errBorder);
                        }

                        agentCard.Child = agentStack;
                        ChatHistory.Children.Add(agentCard);
                        _activeSession?.MessageCards.Add(agentCard);
                        ChatScroller.ScrollToEnd();
                    });
                }
            }
            catch
            {
                if (subagent != null) subagent.Status = "Idle";
                Dispatcher.UIThread.Post(() =>
                {
                    var errCard = new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#201819")),
                        BorderBrush = (IBrush)this.FindResource("GoogleRedBrush")!,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(14, 12),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                    };
                    var errStack = new StackPanel { Spacing = 6 };
                    errStack.Children.Add(new TextBlock
                    {
                        Text = "Execution Notice",
                        FontSize = 12,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = (IBrush)this.FindResource("GoogleRedBrush")!
                    });
                    errStack.Children.Add(new TextBlock
                    {
                        Text = "The agent was unable to execute the requested command. Please verify that the target address is reachable and that the required security tools are accessible in your environment.",
                        FontSize = 12,
                        Foreground = (IBrush)this.FindResource("GoogleTextPrimaryBrush")!,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 18
                    });
                    errCard.Child = errStack;
                    ChatHistory.Children.Add(errCard);
                    _activeSession?.MessageCards.Add(errCard);
                    ChatScroller.ScrollToEnd();
                });
            }
        });
    }
}
