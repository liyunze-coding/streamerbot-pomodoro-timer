// Author: rythondev , https://twitch.tv/rythondev , https://x.com/rythondev, https://ko-fi.com/rython
// Contact: rythondev@gmail.com , or on the above mentioned social media.
//
// This code is licensed under the GNU General Public License Version 3 (GPLv3).
//
// For more details, see https://www.gnu.org/licenses/gpl-3.0.en.html.
using System;
using Newtonsoft.Json;

#region Configuration
public class PomodoroConfig
{
    // Default values, can be overridden by action arguments (see CPHInline.LoadConfig)
    public int WorkMinutes = 50;
    public int BreakMinutes = 10;
    public int LongBreakMinutes = 15;
    public int LongBreakEvery = 3; // every Nth pomodoro the break is a long break
    public int TotalPomodoros = 5; // pomodoros (work sessions) per full run
    public bool NoLastBreak = true; // true = finish right after the last work session, no final break
    public bool BrowserSourceSound = true; // true = sound on browser source | false = sound on streamer.bot

    public string WorkSoundFilePath = "";
    public string BreakSoundFilePath = "";
    public string LongBreakSoundFilePath = "";
    public string EndSoundFilePath = "";

    // Pre-work-end ad break (fires once per work phase when remaining time crosses the threshold)
    public bool EnablePreWorkEndAds = true;
    public int AdDurationSeconds = 180; // 30, 60, 90, 120, 150, or 180
    public string PreWorkEndActionId = "b8b06b77-5348-4c60-9699-093f3b26cc5c";
    public string PreBreakEndActionId = "e6d6ce65-db6c-4970-8e9e-ed3afb19c489";

    // Pre-break-end action (fires once per break / long break phase)
    public int PreBreakEndWarningSeconds = 30;
}

#endregion
#region Data Models
public enum PomodoroPhase
{
    Work,
    Break,
    LongBreak,
    Finished
}

public class PomodoroResponse
{
    public bool Success;
    public string Message;
    public PomodoroResponse(bool success, string message)
    {
        this.Success = success;
        this.Message = message;
    }
}

#endregion
#region Pomodoro Engine
public class PomodoroEngine
{
    private readonly object stateLock = new object();
    private readonly Action<string, object> broadcast; // (eventName, stateSnapshot)
    private readonly Action<object> persistTimerStatus;
    private readonly Action onWorkStart;
    private readonly Action onBreakStart;
    private readonly Action onLongBreakStart;
    private readonly Action onTimerEnd;
    private readonly Action<int> onPreWorkEnd;
    private readonly Action onPreBreakEnd;
    private bool preWorkEndWarningFired;
    private bool preBreakEndWarningFired;
    private System.Timers.Timer phaseTimer;
    private System.Timers.Timer statusTickTimer;
    private PomodoroConfig config = new PomodoroConfig();
    private PomodoroPhase phase = PomodoroPhase.Work;
    private int currentPomodoro = 0; // 1-based once running
    private bool paused = false;
    private DateTime phaseEndsAt = DateTime.MinValue;
    private double remainingMsAtPause = 0;
    private double currentPhaseDurationMs = 0;
    public PomodoroEngine(
        Action<string, object> broadcast,
        Action<object> persistTimerStatus,
        Action onWorkStart,
        Action onBreakStart,
        Action onLongBreakStart,
        Action onTimerEnd,
        Action<int> onPreWorkEnd,
        Action onPreBreakEnd)
    {
        this.broadcast = broadcast;
        this.persistTimerStatus = persistTimerStatus;
        this.onWorkStart = onWorkStart;
        this.onBreakStart = onBreakStart;
        this.onLongBreakStart = onLongBreakStart;
        this.onTimerEnd = onTimerEnd;
        this.onPreWorkEnd = onPreWorkEnd;
        this.onPreBreakEnd = onPreBreakEnd;
        ResetToReady();
    }

#region Public Commands
    public PomodoroResponse Start(PomodoroConfig newConfig)
    {
        lock (stateLock)
        {
            if (IsActive())
                return new PomodoroResponse(false, "❌ A pomodoro session is already running! Use !timer pause, !timer skip, or wait for it to finish.");
            config = newConfig;
            currentPomodoro = 1;
            paused = false;
            EnterPhase(PomodoroPhase.Work);
            broadcast("start", GetSnapshot());
            return new PomodoroResponse(true, $"🍅 Pomodoro started! Work session 1/{config.TotalPomodoros} ({config.WorkMinutes} min). Let's get it!");
        }
    }

    public PomodoroResponse Pause()
    {
        lock (stateLock)
        {
            if (!IsActive())
                return new PomodoroResponse(false, "❌ No pomodoro session is running.");
            if (paused)
                return new PomodoroResponse(false, "❌ The timer is already paused.");
            paused = true;
            remainingMsAtPause = Math.Max(0, (phaseEndsAt - DateTime.UtcNow).TotalMilliseconds);
            StopPhaseTimer();
            EnsureStatusTick();
            broadcast("pause", GetSnapshot());
            return new PomodoroResponse(true, $"⏸️ Timer paused with {FormatRemaining(remainingMsAtPause)} left in {PhaseLabel(phase)}.");
        }
    }

    public PomodoroResponse Resume()
    {
        lock (stateLock)
        {
            if (!IsActive())
                return new PomodoroResponse(false, "❌ No pomodoro session is running.");
            if (!paused)
                return new PomodoroResponse(false, "❌ The timer is not paused.");
            paused = false;
            ScheduleTimer(remainingMsAtPause);
            broadcast("resume", GetSnapshot());
            return new PomodoroResponse(true, $"▶️ Timer resumed! {FormatRemaining(remainingMsAtPause)} left in {PhaseLabel(phase)}.");
        }
    }

    public PomodoroResponse Skip()
    {
        lock (stateLock)
        {
            if (!IsActive())
                return new PomodoroResponse(false, "❌ No pomodoro session is running.");
            const double skipRemainingMs = 1000;
            if (paused)
            {
                StopPhaseTimer();
                remainingMsAtPause = skipRemainingMs;
                phaseEndsAt = DateTime.MinValue;
                EnsureStatusTick();
            }
            else
            {
                ScheduleTimer(skipRemainingMs);
            }
            broadcast("skip", GetSnapshot());
            return new PomodoroResponse(true, $"⏭️ Skipping {PhaseLabel(phase)} — 1 second left.");
        }
    }

    public PomodoroResponse Stop()
    {
        lock (stateLock)
        {
            if (!IsActive())
                return new PomodoroResponse(false, "❌ No pomodoro session is running.");
            ResetToReady();
            broadcast("stop", GetSnapshot());
            return new PomodoroResponse(true, "🛑 Pomodoro session stopped.");
        }
    }

    public PomodoroResponse Reset()
    {
        lock (stateLock)
        {
            ResetToReady();
            broadcast("stop", GetSnapshot());
            return new PomodoroResponse(true, "🔄 Pomodoro reset.");
        }
    }

    public PomodoroResponse SetGoal(int totalPomodoros)
    {
        lock (stateLock)
        {
            if (totalPomodoros < 1)
                return new PomodoroResponse(false, "❌ The goal must be at least 1 pomodoro.");
            if (totalPomodoros < currentPomodoro)
                return new PomodoroResponse(false, $"❌ The goal can't be lower than the current pomodoro ({currentPomodoro}).");
            config.TotalPomodoros = totalPomodoros;
            broadcast("setGoal", GetSnapshot());
            if (!IsActive())
                return new PomodoroResponse(true, $"🎯 Goal set to {config.TotalPomodoros} pomodoros.");
            return new PomodoroResponse(true, $"🎯 Goal updated! Now on pomodoro {currentPomodoro}/{config.TotalPomodoros}.");
        }
    }

    public PomodoroResponse SetCycle(int pomodoro)
    {
        lock (stateLock)
        {
            if (!IsActive())
                return new PomodoroResponse(false, "❌ No pomodoro session is running.");
            if (pomodoro < 1 || pomodoro > config.TotalPomodoros)
                return new PomodoroResponse(false, $"❌ The cycle must be between 1 and {config.TotalPomodoros}.");
            currentPomodoro = pomodoro;
            broadcast("setCycle", GetSnapshot());
            return new PomodoroResponse(true, $"🔄 Cycle updated! Now on pomodoro {currentPomodoro}/{config.TotalPomodoros}.");
        }
    }

    public PomodoroResponse SetTime(double durationMs)
    {
        lock (stateLock)
        {
            if (!IsActive())
                return new PomodoroResponse(false, "❌ No pomodoro session is running.");
            if (durationMs <= 0)
                return new PomodoroResponse(false, "❌ Time must be greater than zero.");
            ApplyRemainingTime(durationMs, "setTime");
            return new PomodoroResponse(true, $"⏱️ Timer set to {FormatRemaining(durationMs)} in {PhaseLabel(phase)}.");
        }
    }

    public PomodoroResponse AddTime(double deltaMs)
    {
        lock (stateLock)
        {
            if (!IsActive())
                return new PomodoroResponse(false, "❌ No pomodoro session is running.");
            if (deltaMs <= 0)
                return new PomodoroResponse(false, "❌ Time must be greater than zero.");
            double newRemainingMs = GetRemainingMs() + deltaMs;
            ApplyRemainingTime(newRemainingMs, "addTime");
            return new PomodoroResponse(true, $"⏱️ Added {FormatRemaining(deltaMs)} — {FormatRemaining(newRemainingMs)} left in {PhaseLabel(phase)}.");
        }
    }

    public PomodoroResponse SubtractTime(double deltaMs)
    {
        lock (stateLock)
        {
            if (!IsActive())
                return new PomodoroResponse(false, "❌ No pomodoro session is running.");
            if (deltaMs <= 0)
                return new PomodoroResponse(false, "❌ Time must be greater than zero.");
            double newRemainingMs = Math.Max(1000, GetRemainingMs() - deltaMs);
            ApplyRemainingTime(newRemainingMs, "subtractTime");
            return new PomodoroResponse(true, $"⏱️ Subtracted {FormatRemaining(deltaMs)} — {FormatRemaining(newRemainingMs)} left in {PhaseLabel(phase)}.");
        }
    }

    public PomodoroResponse Status()
    {
        lock (stateLock)
        {
            broadcast("status", GetSnapshot());
            if (!IsActive())
            {
                if (phase == PomodoroPhase.Finished)
                    return new PomodoroResponse(true, "🎉 Pomodoro session complete!");
                return new PomodoroResponse(true, $"💤 Ready for work — {config.WorkMinutes} min (use !timer start).");
            }
            double remainingMs = paused
                ? remainingMsAtPause
                : Math.Max(0, (phaseEndsAt - DateTime.UtcNow).TotalMilliseconds);
            if (paused)
                return new PomodoroResponse(true, $"⏸️ Pomodoro {currentPomodoro}/{config.TotalPomodoros} — {PhaseLabel(phase)} — paused with {FormatRemaining(remainingMs)} left.");
            return new PomodoroResponse(true, $"🍅 Pomodoro {currentPomodoro}/{config.TotalPomodoros} — {PhaseLabel(phase)} — {FormatRemaining(remainingMs)} remaining.");
        }
    }

#endregion
#region Phase Logic
    private double GetRemainingMs()
    {
        if (paused)
            return remainingMsAtPause;
        return Math.Max(0, (phaseEndsAt - DateTime.UtcNow).TotalMilliseconds);
    }

    private void ApplyRemainingTime(double durationMs, string eventName)
    {
        currentPhaseDurationMs = durationMs;
        if (paused)
        {
            remainingMsAtPause = durationMs;
            phaseEndsAt = DateTime.MinValue;
        }
        else
        {
            ScheduleTimer(durationMs);
        }
        broadcast(eventName, GetSnapshot());
    }

    private bool IsActive()
    {
        return currentPomodoro > 0
            && (phase == PomodoroPhase.Work || phase == PomodoroPhase.Break || phase == PomodoroPhase.LongBreak);
    }

    private bool IsLongBreakDue()
    {
        return config.LongBreakEvery > 0 && currentPomodoro % config.LongBreakEvery == 0;
    }

    private void EnterPhase(PomodoroPhase newPhase)
    {
        preWorkEndWarningFired = false;
        preBreakEndWarningFired = false;
        phase = newPhase;
        currentPhaseDurationMs = GetPhaseDurationMs(newPhase);
        if (paused)
        {
            remainingMsAtPause = currentPhaseDurationMs;
            phaseEndsAt = DateTime.MinValue;
        }
        else
        {
            ScheduleTimer(currentPhaseDurationMs);
        }
        RaisePhaseStartEvent(newPhase);
    }

    private void RaisePhaseStartEvent(PomodoroPhase newPhase)
    {
        switch (newPhase)
        {
            case PomodoroPhase.Work:
                onWorkStart();
                break;
            case PomodoroPhase.Break:
                onBreakStart();
                break;
            case PomodoroPhase.LongBreak:
                onLongBreakStart();
                break;
        }
    }

    private double GetPhaseDurationMs(PomodoroPhase p)
    {
        switch (p)
        {
            case PomodoroPhase.Work:
                return TimeSpan.FromMinutes(config.WorkMinutes).TotalMilliseconds;
            case PomodoroPhase.Break:
                return TimeSpan.FromMinutes(config.BreakMinutes).TotalMilliseconds;
            case PomodoroPhase.LongBreak:
                return TimeSpan.FromMinutes(config.LongBreakMinutes).TotalMilliseconds;
            default:
                return 0;
        }
    }

    private void AdvancePhase()
    {
        if (phase == PomodoroPhase.Work)
        {
            bool isLastPomodoro = currentPomodoro >= config.TotalPomodoros;
            if (isLastPomodoro && config.NoLastBreak)
            {
                Finish();
                return;
            }

            EnterPhase(IsLongBreakDue() ? PomodoroPhase.LongBreak : PomodoroPhase.Break);
        }
        else // Break or LongBreak
        {
            if (currentPomodoro >= config.TotalPomodoros)
            {
                Finish();
                return;
            }

            currentPomodoro++;
            EnterPhase(PomodoroPhase.Work);
        }
    }

    private void ResetToReady()
    {
        StopTimer();
        phase = PomodoroPhase.Work;
        currentPomodoro = 0;
        paused = false;
        phaseEndsAt = DateTime.MinValue;
        currentPhaseDurationMs = GetPhaseDurationMs(PomodoroPhase.Work);
        preWorkEndWarningFired = false;
        preBreakEndWarningFired = false;
    }

    private void Finish()
    {
        StopTimer();
        phase = PomodoroPhase.Finished;
        paused = false;
        phaseEndsAt = DateTime.MinValue;
        currentPhaseDurationMs = 0;
        onTimerEnd();
        persistTimerStatus(GetSnapshot());
    }

#endregion
#region Timer
    private void ScheduleTimer(double durationMs)
    {
        StopPhaseTimer();
        phaseEndsAt = DateTime.UtcNow.AddMilliseconds(durationMs);
        phaseTimer = new System.Timers.Timer(Math.Max(1, durationMs));
        phaseTimer.AutoReset = false;
        phaseTimer.Elapsed += (sender, e) => OnPhaseElapsed();
        phaseTimer.Start();
        StartStatusTick();
    }

    private void StopPhaseTimer()
    {
        if (phaseTimer != null)
        {
            phaseTimer.Stop();
            phaseTimer.Dispose();
            phaseTimer = null;
        }
    }

    private void StopTimer()
    {
        StopPhaseTimer();
        StopStatusTick();
    }

    private void EnsureStatusTick()
    {
        if (statusTickTimer == null)
            StartStatusTick();
    }

    private void StartStatusTick()
    {
        StopStatusTick();
        statusTickTimer = new System.Timers.Timer(1000);
        statusTickTimer.AutoReset = true;
        statusTickTimer.Elapsed += (sender, e) => OnStatusTick();
        statusTickTimer.Start();
        persistTimerStatus(GetSnapshot());
    }

    private void StopStatusTick()
    {
        if (statusTickTimer != null)
        {
            statusTickTimer.Stop();
            statusTickTimer.Dispose();
            statusTickTimer = null;
        }
    }

    private void OnStatusTick()
    {
        lock (stateLock)
        {
            if (!IsActive())
            {
                StopStatusTick();
                return;
            }
            CheckPhaseEndWarnings();
            persistTimerStatus(GetSnapshot());
        }
    }

    private void CheckPhaseEndWarnings()
    {
        if (paused)
            return;

        double remainingMs = GetRemainingMs();
        if (remainingMs <= 0)
            return;

        if (phase == PomodoroPhase.Work
            && config.EnablePreWorkEndAds
            && config.AdDurationSeconds > 0
            && !preWorkEndWarningFired)
        {
            double thresholdMs = TimeSpan.FromSeconds(config.AdDurationSeconds).TotalMilliseconds + 5000; // compensate for 5 seconds delay
            if (remainingMs <= thresholdMs)
            {
                preWorkEndWarningFired = true;
                onPreWorkEnd(config.AdDurationSeconds);
            }
        }

        if ((phase == PomodoroPhase.Break || phase == PomodoroPhase.LongBreak)
            && !preBreakEndWarningFired)
        {
            double thresholdMs = TimeSpan.FromSeconds(config.PreBreakEndWarningSeconds).TotalMilliseconds;
            if (remainingMs <= thresholdMs)
            {
                preBreakEndWarningFired = true;
                onPreBreakEnd();
            }
        }
    }

    private void OnPhaseElapsed()
    {
        lock (stateLock)
        {
            if (!IsActive() || paused)
                return;
            AdvancePhase();
            broadcast(phase == PomodoroPhase.Finished ? "finish" : "phaseChange", GetSnapshot());
        }
    }

#endregion
#region State Snapshot
    public PomodoroConfig GetConfig() => config;

    public object GetSnapshot()
    {
        double remainingMs;
        if (paused)
            remainingMs = remainingMsAtPause;
        else if (IsActive())
            remainingMs = Math.Max(0, (phaseEndsAt - DateTime.UtcNow).TotalMilliseconds);
        else if (phase == PomodoroPhase.Work)
            remainingMs = GetPhaseDurationMs(PomodoroPhase.Work);
        else
            remainingMs = 0;
        return new
        {
            phase = PhaseKey(phase),
            pomodoro = currentPomodoro,
            totalPomodoros = config.TotalPomodoros,
            paused = paused,
            running = IsActive(),
            // epoch ms the current phase ends at; null while paused/inactive (use remainingMs instead)
            endsAt = (!paused && IsActive()) ? (long?)new DateTimeOffset(phaseEndsAt).ToUnixTimeMilliseconds() : null,
            remainingMs = (long)remainingMs,
            phaseDurationMs = (long)currentPhaseDurationMs,
            config = new
            {
                workMinutes = config.WorkMinutes,
                breakMinutes = config.BreakMinutes,
                longBreakMinutes = config.LongBreakMinutes,
                longBreakEvery = config.LongBreakEvery,
                totalPomodoros = config.TotalPomodoros,
                noLastBreak = config.NoLastBreak,
                browserSourceSound = config.BrowserSourceSound,
                enablePreWorkEndAds = config.EnablePreWorkEndAds,
                adDurationSeconds = config.AdDurationSeconds,
                preBreakEndWarningSeconds = config.PreBreakEndWarningSeconds,
            }
        };
    }

    private static string PhaseKey(PomodoroPhase p)
    {
        switch (p)
        {
            case PomodoroPhase.Work: return "work";
            case PomodoroPhase.Break: return "break";
            case PomodoroPhase.LongBreak: return "longBreak";
            case PomodoroPhase.Finished: return "finished";
            default: return "work";
        }
    }

    private static string PhaseLabel(PomodoroPhase p)
    {
        switch (p)
        {
            case PomodoroPhase.Work: return "work";
            case PomodoroPhase.Break: return "break";
            case PomodoroPhase.LongBreak: return "long break";
            case PomodoroPhase.Finished: return "finished";
            default: return "work";
        }
    }

    private static string FormatRemaining(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s" : $"{ts.Minutes}m {ts.Seconds}s";
    }
}

#endregion
#region Main Command Handler
public class CPHInline
{
    private const string TimerStatusGlobalVar = "timer-status";
    private static PomodoroEngine engine;
    private static readonly object initLock = new object();
    public void Init()
    {
        lock (initLock)
        {
            if (engine == null)
                engine = new PomodoroEngine(Broadcast, PersistTimerStatus, OnWorkStart, OnBreakStart, OnLongBreakStart, OnTimerEnd, OnPreWorkEnd, OnPreBreakEnd);
        }

        // Recreate timer-status if it was deleted from Streamer.bot globals.
        EnsureTimerStatusGlobal(engine.GetSnapshot());
    }

#region Events
    // Fired when a work session starts (including pomodoro 1 on !timer start).
    private void OnWorkStart()
    {
        var config = engine.GetConfig();
        // if (!config.BrowserSourceSound)
        // {
        //     CPH.PlaySound(config.WorkSoundFilePath);
        // }
        
        CPH.RunActionById("ad25831a-6f84-4d5c-ab00-ec141d09a657");
    }

    // Fired when a regular break starts.
    private void OnBreakStart()
    {
        var config = engine.GetConfig();
        if (!config.BrowserSourceSound)
        {
            CPH.PlaySound(config.BreakSoundFilePath);
        }
            

        CPH.RunActionById("58fdf247-faab-40c9-8651-0f8650801913");
    }

    // Fired when a long break starts.
    private void OnLongBreakStart()
    {
        var config = engine.GetConfig();
        if (!config.BrowserSourceSound)
        {
            CPH.PlaySound(config.LongBreakSoundFilePath);
        }

        CPH.RunActionById("22aef312-66d1-4579-bc73-e6eb59743d5c");
    }

    // Fired when the full pomodoro run completes (all pomodoros done).
    private void OnTimerEnd()
    {
        var config = engine.GetConfig();
        if (!config.BrowserSourceSound)
        {
            CPH.PlaySound(config.EndSoundFilePath);
        }

        CPH.RunActionById("49398044-2fdb-40b8-a953-cec60c22cce2");
    }

    // Fired once per work phase when remaining time crosses ad duration.
    // Streamer.bot action receives adDurationSeconds (30–180, step 30).
    private void OnPreWorkEnd(int adDurationSeconds)
    {
        var config = engine.GetConfig();
        if (string.IsNullOrWhiteSpace(config.PreWorkEndActionId))
            return;

        CPH.SetArgument("adDurationSeconds", adDurationSeconds);
        CPH.RunActionById(config.PreWorkEndActionId);
    }

    // Fired once per break phase when remaining time crosses preBreakEndWarningSeconds.
    private void OnPreBreakEnd()
    {
        var config = engine.GetConfig();
        
        if (string.IsNullOrWhiteSpace(config.PreBreakEndActionId))
            return;
        CPH.RunActionById(config.PreBreakEndActionId);
    }
#endregion
#region Platform Helpers
    private static string SerializeTimerStatus(string eventName, object state)
    {
        return JsonConvert.SerializeObject(new { source = "rython-pomodoro-timer", @event = eventName, state = state });
    }

    private void Broadcast(string eventName, object state)
    {
        CPH.WebsocketBroadcastJson(SerializeTimerStatus(eventName, state));
        EnsureTimerStatusGlobal(state);
    }

    private void PersistTimerStatus(object state)
    {
        EnsureTimerStatusGlobal(state);
    }

    private void EnsureTimerStatusGlobal(object state)
    {
        CPH.SetGlobalVar(TimerStatusGlobalVar, SerializeTimerStatus("tick", state), true);
    }

    // Reads configuration from action arguments, falling back to PomodoroConfig defaults.
    // Set these as arguments on the Streamer.bot action to configure:
    //   workMinutes, breakMinutes, longBreakMinutes, longBreakEvery, totalPomodoros, noLastBreak,
    //   enablePreWorkEndAds, adDurationSeconds, PreWorkEndActionId,
    //   preBreakEndWarningSeconds
    private static PomodoroConfig CopyConfig(PomodoroConfig source)
    {
        return new PomodoroConfig
        {
            WorkMinutes = source.WorkMinutes,
            BreakMinutes = source.BreakMinutes,
            LongBreakMinutes = source.LongBreakMinutes,
            LongBreakEvery = source.LongBreakEvery,
            TotalPomodoros = source.TotalPomodoros,
            NoLastBreak = source.NoLastBreak,
            BrowserSourceSound = source.BrowserSourceSound,
            WorkSoundFilePath = source.WorkSoundFilePath,
            BreakSoundFilePath = source.BreakSoundFilePath,
            LongBreakSoundFilePath = source.LongBreakSoundFilePath,
            EndSoundFilePath = source.EndSoundFilePath,
            EnablePreWorkEndAds = source.EnablePreWorkEndAds,
            AdDurationSeconds = source.AdDurationSeconds,
            PreWorkEndActionId = source.PreWorkEndActionId,
            PreBreakEndActionId = source.PreBreakEndActionId,
            PreBreakEndWarningSeconds = source.PreBreakEndWarningSeconds,
        };
    }

    private PomodoroConfig LoadConfig(PomodoroConfig baseConfig = null)
    {
        var config = CopyConfig(baseConfig ?? new PomodoroConfig());
        if (CPH.TryGetArg("workMinutes", out string workMinutes) && int.TryParse(workMinutes, out int work) && work > 0)
            config.WorkMinutes = work;
        if (CPH.TryGetArg("breakMinutes", out string breakMinutes) && int.TryParse(breakMinutes, out int brk) && brk > 0)
            config.BreakMinutes = brk;
        if (CPH.TryGetArg("longBreakMinutes", out string longBreakMinutes) && int.TryParse(longBreakMinutes, out int longBrk) && longBrk > 0)
            config.LongBreakMinutes = longBrk;
        if (CPH.TryGetArg("longBreakEvery", out string longBreakEvery) && int.TryParse(longBreakEvery, out int every) && every > 0)
            config.LongBreakEvery = every;
        if (CPH.TryGetArg("totalPomodoros", out string totalPomodoros) && int.TryParse(totalPomodoros, out int total) && total > 0)
            config.TotalPomodoros = total;
        if (CPH.TryGetArg("noLastBreak", out string noLastBreak) && bool.TryParse(noLastBreak, out bool noLast))
            config.NoLastBreak = noLast;
        if (CPH.TryGetArg("browserSourceSound", out string browserSourceSound) && bool.TryParse(browserSourceSound, out bool browserSound))
            config.BrowserSourceSound = browserSound;
        if (CPH.TryGetArg("workSoundFilePath", out string workSound) && !string.IsNullOrWhiteSpace(workSound))
            config.WorkSoundFilePath = workSound;
        if (CPH.TryGetArg("breakSoundFilePath", out string breakSound) && !string.IsNullOrWhiteSpace(breakSound))
            config.BreakSoundFilePath = breakSound;
        if (CPH.TryGetArg("longBreakSoundFilePath", out string longBreakSound) && !string.IsNullOrWhiteSpace(longBreakSound))
            config.LongBreakSoundFilePath = longBreakSound;
        if (CPH.TryGetArg("enablePreWorkEndAds", out string enablePreWorkEndAds) && bool.TryParse(enablePreWorkEndAds, out bool preWorkAds))
            config.EnablePreWorkEndAds = preWorkAds;
        if (CPH.TryGetArg("adDurationSeconds", out string adDurationSeconds) && int.TryParse(adDurationSeconds, out int adSeconds))
            config.AdDurationSeconds = NormalizeAdDurationSeconds(adSeconds);
        if (CPH.TryGetArg("PreWorkEndActionId", out string PreWorkEndActionId) && !string.IsNullOrWhiteSpace(PreWorkEndActionId))
            config.PreWorkEndActionId = PreWorkEndActionId;
        if (CPH.TryGetArg("preBreakEndWarningSeconds", out string preBreakEndWarningSeconds) && int.TryParse(preBreakEndWarningSeconds, out int warnSeconds) && warnSeconds >= 0)
            config.PreBreakEndWarningSeconds = warnSeconds;
        return config;
    }

    // Ad duration must be 0 (disabled) or 30–180 in 30-second steps.
    private static int NormalizeAdDurationSeconds(int seconds)
    {
        if (seconds <= 0)
            return 0;
        seconds = Math.Min(180, Math.Max(30, seconds));
        return ((seconds + 15) / 30) * 30;
    }

    private void Respond(string message)
    {
        CPH.TryGetArg("userType", out string platform);
        switch (platform)
        {
            case "twitch":
                CPH.TryGetArg("msgId", out string twitchMsgId);
                CPH.TwitchReplyToMessage(message, twitchMsgId);
                break;
            case "youtube":
                CPH.TryGetArg("user", out string YTUser);
                CPH.SendYouTubeMessage($"@{YTUser} {message}");
                break;
            case "kick":
                CPH.TryGetArg("user", out string kickUser);
                CPH.TryGetArg("msgId", out string kickMsgId);
                CPH.KickReplyToMessage($"@{kickUser} {message}", kickMsgId);
                break;
            default:
                break;
        }
    }

    // Parses the first word after the command (Streamer.bot puts it in "input0") as an integer.
    private bool TryGetIntInput(out int value)
    {
        value = 0;
        return CPH.TryGetArg("input0", out string input) && int.TryParse(input, out value);
    }

    // Parses mm:ss or hh:mm:ss from input0.
    private bool TryGetTimeInput(out double durationMs)
    {
        durationMs = 0;
        return CPH.TryGetArg("input0", out string input) && TryParseDuration(input, out durationMs);
    }

    private static bool TryParseDuration(string input, out double durationMs)
    {
        durationMs = 0;
        if (string.IsNullOrWhiteSpace(input))
            return false;
        string[] parts = input.Trim().Split(':');
        int hours = 0, minutes = 0, seconds = 0;
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[0], out minutes) || !int.TryParse(parts[1], out seconds))
                return false;
            if (minutes < 0 || seconds < 0 || seconds >= 60)
                return false;
        }
        else if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], out hours) || !int.TryParse(parts[1], out minutes) || !int.TryParse(parts[2], out seconds))
                return false;
            if (hours < 0 || minutes < 0 || seconds < 0 || minutes >= 60 || seconds >= 60)
                return false;
        }
        else
        {
            return false;
        }
        durationMs = TimeSpan.FromHours(hours).Add(TimeSpan.FromMinutes(minutes)).Add(TimeSpan.FromSeconds(seconds)).TotalMilliseconds;
        return durationMs > 0;
    }

#endregion
#region Commands
    public bool StartCommand()
    {
        var response = engine.Start(LoadConfig(engine.GetConfig()));
        Respond(response.Message);
        return response.Success;
    }

    public bool PauseCommand()
    {
        var response = engine.Pause();
        Respond(response.Message);
        return response.Success;
    }

    public bool ResumeCommand()
    {
        var response = engine.Resume();
        Respond(response.Message);
        return response.Success;
    }

    public bool SkipCommand()
    {
        var response = engine.Skip();
        Respond(response.Message);
        return response.Success;
    }

    public bool StopCommand()
    {
        var response = engine.Stop();
        Respond(response.Message);
        return response.Success;
    }

    public bool ResetCommand()
    {
        var response = engine.Reset();
        Respond(response.Message);
        return response.Success;
    }

    public bool SetGoalCommand()
    {
        if (!TryGetIntInput(out int goal))
        {
            Respond("❌ Usage: !timer goal <number> (e.g. !timer goal 6)");
            return false;
        }

        var response = engine.SetGoal(goal);
        Respond(response.Message);
        return response.Success;
    }

    public bool SetCycleCommand()
    {
        if (!TryGetIntInput(out int cycle))
        {
            Respond("❌ Usage: !timer cycle <number> (e.g. !timer cycle 2)");
            return false;
        }

        var response = engine.SetCycle(cycle);
        Respond(response.Message);
        return response.Success;
    }

    public bool SetTimeCommand()
    {
        if (!TryGetTimeInput(out double durationMs))
        {
            Respond("❌ Usage: !pomotime <mm:ss> or <hh:mm:ss> (e.g. !timer set 25:30 or !timer set 1:05:00)");
            return false;
        }

        var response = engine.SetTime(durationMs);
        Respond(response.Message);
        return response.Success;
    }

    public bool AddTimeCommand()
    {
        if (!TryGetTimeInput(out double durationMs))
        {
            Respond("❌ Usage: !timer add <mm:ss> or <hh:mm:ss> (e.g. !timer add 5:00 or !timer add 1:05:00)");
            return false;
        }

        var response = engine.AddTime(durationMs);
        Respond(response.Message);
        return response.Success;
    }

    public bool SubtractTimeCommand()
    {
        if (!TryGetTimeInput(out double durationMs))
        {
            Respond("❌ Usage: !timer sub <mm:ss> or <hh:mm:ss> (e.g. !timer sub 5:00 or !timer sub 1:05:00)");
            return false;
        }

        var response = engine.SubtractTime(durationMs);
        Respond(response.Message);
        return response.Success;
    }

    // Re-broadcasts current state to the widget and replies in chat.
    public bool StatusCommand()
    {
        var response = engine.Status();
        Respond(response.Message);
        return response.Success;
    }
#endregion
}
#endregion
