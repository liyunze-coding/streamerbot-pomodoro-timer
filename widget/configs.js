"use strict";

const configs = (function () {
	const streamerBotSettings = {
		host: "127.0.0.1",
		port: 6968,
		endpoint: "/",
		password: "", // set if Streamer.bot websocket auth is enabled
	};

	const phaseLabels = {
		idle: "Idle",
		work: "Work",
		break: "Break",
		longBreak: "Long Break",
		finished: "Done",
	};

	const settings = {
		phaseLabels,
	};

	return {
		streamerBotSettings,
		settings,
	};
})();
