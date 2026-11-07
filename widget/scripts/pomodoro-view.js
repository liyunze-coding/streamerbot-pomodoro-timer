const phaseLabels = configs.settings.phaseLabels;

const timerEl = document.getElementById("timer");
const phaseEl = document.getElementById("phase-label");
const countEl = document.getElementById("pomodoro-count");

const PHASE_SOUND_GRACE_MS = 2500;

let state = null;
let tickInterval = null;
let lastAsOf = 0;
let lastPlayedPhaseStart = null;

const phaseSounds = Object.fromEntries(
	Object.entries(configs.settings.sounds).map(([key, src]) => [
		key,
		Object.assign(new Audio(src), { preload: "auto" }),
	]),
);

function playPhaseSound(phase) {
	const soundKey =
		phase === "finished"
			? "end"
			: phase === "longBreak"
				? "longBreak"
				: phase;

	const audio = phaseSounds[soundKey];
	if (!audio) return;

	audio.currentTime = 0;
	audio.play().catch((err) => console.warn("Sound play failed:", err));
}

function maybePlayPhaseSound(newState) {
	if (!configs.settings.browserSourceSound) return;
	if (!newState?.config?.browserSourceSound) return;
	// A hidden OBS browser source freezes this page. Queued audio would then
	// play when the scene is shown, seconds after the C# clock already moved on.
	if (document.hidden) return;

	const startedAt = newState.phaseStartedAt;
	if (startedAt == null) return;

	const soundKey = `${newState.phase}:${startedAt}`;
	if (lastPlayedPhaseStart === soundKey) return;
	if (Math.abs(Date.now() - startedAt) > PHASE_SOUND_GRACE_MS) return;

	lastPlayedPhaseStart = soundKey;
	playPhaseSound(newState.phase);
}

function getReadyState() {
	return {
		phase: "work",
		pomodoro: 0,
		totalPomodoros: configs.settings.totalPomodoros,
		paused: false,
		running: false,
		remainingMs: configs.settings.workMinutes * 60 * 1000,
	};
}

function formatTime(ms) {
	const totalSeconds = Math.max(0, Math.ceil(ms / 1000));
	const hours = Math.floor(totalSeconds / 3600);
	const minutes = Math.floor((totalSeconds % 3600) / 60);
	const seconds = totalSeconds % 60;
	const pad = (n) => String(n).padStart(2, "0");

	if (hours > 0) {
		return `${hours}:${pad(minutes)}:${pad(seconds)}`;
	}

	return `${pad(minutes)}:${pad(seconds)}`;
}

function getRemainingMs(currentState) {
	if (!currentState) return 0;

	if (currentState.paused || currentState.endsAt == null) {
		return currentState.remainingMs ?? 0;
	}

	return Math.max(0, currentState.endsAt - Date.now());
}

function render() {
	const displayState = state ?? getReadyState();

	timerEl.textContent = formatTime(getRemainingMs(displayState));

	let label = phaseLabels[displayState.phase] ?? displayState.phase;
	if (displayState.paused && displayState.running) {
		label += " (Paused)";
	}

	phaseEl.textContent = label;
	countEl.textContent = `Session ${displayState.pomodoro}/${displayState.totalPomodoros}`;
	document.body.dataset.phase = displayState.phase;

	if (displayState.paused) {
		document.body.dataset.paused = "true";
	} else {
		delete document.body.dataset.paused;
	}
}

function startTick() {
	stopTick();
	let lastFrameAt = Date.now();
	tickInterval = setInterval(() => {
		const now = Date.now();
		const wokeFromHidden = now - lastFrameAt > 1000;
		lastFrameAt = now;
		if (wokeFromHidden && typeof refreshTimerState === "function") {
			refreshTimerState();
		}
		render();
	}, 250);
}

function stopTick() {
	if (tickInterval) {
		clearInterval(tickInterval);
		tickInterval = null;
	}
}

function applyPomodoroState(newState) {
	if (newState?.asOf != null && lastAsOf > newState.asOf) return;

	if (newState?.asOf != null) lastAsOf = newState.asOf;
	state = newState;
	render();
	maybePlayPhaseSound(newState);

	if (state?.running && !state.paused) {
		if (!tickInterval) startTick();
	} else {
		stopTick();
	}
}

render();
