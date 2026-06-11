const POMODORO_SOURCE = "rython-pomodoro-timer";

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
	const existing = document.getElementById("connection-error");
	if (existing) existing.remove();
	await refreshTimerState();
}

async function refreshTimerState() {
	const response = await client.getGlobal("timer-status", true);
	if (response.status !== "ok" || !response.variable?.value) return;

	try {
		onStreamerbotData(JSON.parse(response.variable.value));
	} catch (err) {
		console.error("Failed to parse timer-status global:", err);
	}
}

function onDisconnect() {
	showConnectionError("Connection Failed: Unable to connect to Streamer.bot");
}

function onError(err) {
	showConnectionError(
		"Connection Failed: " + (err?.message || "Unknown error"),
	);
}

function showConnectionError(message) {
	const existing = document.getElementById("connection-error");
	if (existing) existing.remove();

	const popup = document.createElement("div");
	popup.id = "connection-error";
	popup.textContent = message;
	Object.assign(popup.style, {
		position: "fixed",
		top: "20px",
		left: "50%",
		transform: "translateX(-50%)",
		background: "#e53935",
		color: "#fff",
		padding: "12px 24px",
		borderRadius: "8px",
		fontFamily: configs.styles.fontFamily,
		fontSize: "1.1rem",
		fontWeight: "700",
		zIndex: "9999",
		boxShadow: "0 4px 12px rgba(0,0,0,0.4)",
		textAlign: "center",
	});
	document.body.appendChild(popup);

	setTimeout(() => {
		popup.animate([{ opacity: 1 }, { opacity: 0 }], {
			duration: 300,
			fill: "forwards",
		}).onfinish = () => popup.remove();
	}, 5000);
}

client.on('General.Custom', ({ _event, data }) => onStreamerbotData(data));

function onStreamerbotData(data) {
	console.log(data);
	if (data?.source !== POMODORO_SOURCE || !data.state) return;
	applyPomodoroState(data.state);
}
