"use strict";

const configs = (function () {
	const streamerBotSettings = {
		host: "127.0.0.1",
		port: 6968,
		endpoint: "/",
		password: "", // set if Streamer.bot websocket auth is enabled
	};

	const phaseLabels = {
		work: "Work",
		break: "Break",
		longBreak: "Long Break",
		finished: "Done",
	};

	const sounds = {
		work: "../sounds/work-started.mp3",
		break: "../sounds/break-started.mp3",
		longBreak: "../sounds/break-started.mp3",
		end: "../sounds/break-started.mp3",
	};

	const settings = {
		phaseLabels,
		sounds,
		workMinutes: 50,
		totalPomodoros: 5,
	};

	return {
		streamerBotSettings,
		settings,
	};
})();
