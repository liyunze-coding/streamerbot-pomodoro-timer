const styles = configs.styles;
const phaseLabels = configs.settings.phaseLabels;

const timerEl = document.getElementById("timer");
const phaseEl = document.getElementById("phase-label");
const countEl = document.getElementById("pomodoro-count");

let state = null;
let tickInterval = null;

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
	if (!state) {
		timerEl.textContent = "00:00";
		phaseEl.textContent = phaseLabels.idle;
		countEl.textContent = "Session 0/0";
		document.body.dataset.phase = "idle";
		delete document.body.dataset.paused;
		return;
	}

	timerEl.textContent = formatTime(getRemainingMs(state));

	let label = phaseLabels[state.phase] ?? state.phase;
	if (state.paused && state.running) {
		label += " (Paused)";
	}

	phaseEl.textContent = label;
	countEl.textContent = `Session ${state.pomodoro}/${state.totalPomodoros}`;
	document.body.dataset.phase = state.phase;

	if (state.paused) {
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
