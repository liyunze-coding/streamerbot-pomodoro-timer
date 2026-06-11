const styles = configs.styles;
const phaseLabels = configs.settings.phaseLabels;

const timerEl = document.getElementById("timer");
const phaseEl = document.getElementById("phase-label");
const countEl = document.getElementById("pomodoro-count");

let state = null;
let tickInterval = null;

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
	tickInterval = setInterval(render, 250);
}

function stopTick() {
	if (tickInterval) {
		clearInterval(tickInterval);
		tickInterval = null;
	}
}

function applyPomodoroState(newState) {
	state = newState;
	render();

	if (state?.running && !state.paused) {
		startTick();
	} else {
		stopTick();
	}
}

render();
