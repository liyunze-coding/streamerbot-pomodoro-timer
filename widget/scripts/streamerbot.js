const POMODORO_SOURCE = "rython-pomodoro-timer";

const connectionErrorEl = document.getElementById("connection-error");
const connectionErrorMessageEl = connectionErrorEl?.querySelector(
	".connection-error__message",
);

const clientOptions = {
	host: configs.streamerBotSettings.host,
	port: configs.streamerBotSettings.port,
	endpoint: configs.streamerBotSettings.endpoint,
	onConnect: onConnect,
	onDisconnect: onDisconnect,
	onError: onError,
};

if (configs.streamerBotSettings.password) {
	clientOptions.password = configs.streamerBotSettings.password;
}

const client = new StreamerbotClient(clientOptions);

async function onConnect() {
	hideConnectionError();
	await refreshTimerState();
}

async function refreshTimerState() {
	const response = await client.getGlobal("timer-status", true);
	if (response.status !== "ok" || !response.variable?.value) return;

	try {
		onStreamerbotData(JSON.parse(response.variable.value), { silent: true });
	} catch (err) {
		console.error("Failed to parse timer-status global:", err);
	}
}

function onDisconnect() {
	showConnectionError("Unable to connect to Streamer.bot");
}

function onError(err) {
	showConnectionError(err?.message || "Unknown error");
}

function showConnectionError(message) {
	if (!connectionErrorEl) return;

	if (connectionErrorMessageEl) {
		connectionErrorMessageEl.textContent = message;
	}

	connectionErrorEl.hidden = false;
}

function hideConnectionError() {
	if (!connectionErrorEl) return;
	connectionErrorEl.hidden = true;
}

client.on("General.Custom", ({ _event, data }) => onStreamerbotData(data));

function onStreamerbotData(data, { silent = false } = {}) {
	if (data?.source !== POMODORO_SOURCE || !data.state) return;
	applyPomodoroState(data.state, { silent });
}
