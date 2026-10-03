let currentUsername = "";
let selectedUserId = null;
let selectedUsername = "";
let activeMessageId = null;
let lastUsers = [];
const chats = {}; // userId -> [{ id, from, text, mine, edited }]

document.getElementById("users").style.display = "none";
document.getElementById("chat").style.display = "none";

const connection = new signalR.HubConnectionBuilder()
    .withUrl("/messenger")
    .withAutomaticReconnect()
    .build();

const startPromise = connection.start().catch(e => console.error(e.toString()));

function findMessage(id) {
    for (const key in chats) {
        const m = chats[key].find(x => x.id === id);
        if (m) return { m, key };
    }
    return null;
}

function renderUsers() {
    const userList = document.getElementById("userList");
    userList.innerHTML = "";

    lastUsers.forEach(user => {
        if (user.username === currentUsername) return;

        const li = document.createElement("li");
        li.textContent = (user.online ? "В сети " : "Не в сети ") + user.username;
        li.style.cursor = "pointer";
        if (user.id === selectedUserId) li.style.fontWeight = "bold";

        li.addEventListener("click", () => openChat(user));
        userList.appendChild(li);
    });
}

function renderMessages() {
    const list = document.getElementById("messagesList");
    list.innerHTML = "";

    const messages = chats[selectedUserId] || [];
    messages.forEach(m => {
        const li = document.createElement("li");
        li.style.margin = "4px 0";

        const span = document.createElement("span");
        span.textContent = `${m.from}: ${m.text}` + (m.edited ? " (изменено)" : "");

        if (m.mine) {
            span.style.cursor = "pointer";
            span.addEventListener("click", (e) => {
                e.stopPropagation();
                activeMessageId = activeMessageId === m.id ? null : m.id;
                renderMessages();
            });
        }
        li.appendChild(span);

        if (m.mine && activeMessageId === m.id) {
            const actions = document.createElement("div");
            actions.style.margin = "4px 0 0 12px";

            const editBtn = document.createElement("button");
            editBtn.textContent = "Редактировать";
            editBtn.addEventListener("click", (e) => {
                e.stopPropagation();
                const newText = prompt("Изменить сообщение:", m.text);
                activeMessageId = null;
                if (newText !== null) {
                    const trimmed = newText.trim();
                    if (trimmed && trimmed !== m.text) {
                        connection.invoke("EditMessage", m.id, trimmed)
                            .catch(err => console.error(err.toString()));
                    }
                }
                renderMessages();
            });

            const delBtn = document.createElement("button");
            delBtn.textContent = "Удалить";
            delBtn.style.marginLeft = "6px";
            delBtn.addEventListener("click", (e) => {
                e.stopPropagation();
                activeMessageId = null;
                if (confirm("Удалить сообщение?")) {
                    connection.invoke("DeleteMessage", m.id)
                        .catch(err => console.error(err.toString()));
                }
                renderMessages();
            });

            actions.appendChild(editBtn);
            actions.appendChild(delBtn);
            li.appendChild(actions);
        }

        list.appendChild(li);
    });
}

async function openChat(user) {
    selectedUserId = user.id;
    selectedUsername = user.username;
    activeMessageId = null;
    renderUsers();
    document.getElementById("chatTitle").textContent = "Чат с " + user.username;
    document.getElementById("chat").style.display = "block";

    try {
        const history = await connection.invoke("GetHistory", user.id);
        chats[user.id] = history.map(m => ({
            id: m.id,
            from: m.fromMe ? "Я" : user.username,
            text: m.text,
            mine: m.fromMe,
            edited: m.isEdited
        }));
    } catch (err) {
        console.error(err.toString());
    }

    renderMessages();
}

connection.on("UpdateUsers", (users) => {
    lastUsers = users;
    renderUsers();
});

connection.on("ReceiveMessage", (fromUserId, fromUsername, messageId, text) => {
    if (!chats[fromUserId]) chats[fromUserId] = [];
    chats[fromUserId].push({
        id: messageId, from: fromUsername, text, mine: false, edited: false
    });

    if (fromUserId === selectedUserId) renderMessages();
});

connection.on("MessageSent", (toUserId, messageId, text) => {
    if (!chats[toUserId]) chats[toUserId] = [];
    chats[toUserId].push({
        id: messageId, from: "Я", text, mine: true, edited: false
    });

    if (toUserId === selectedUserId) renderMessages();
});

connection.on("MessageEdited", (messageId, newText) => {
    const found = findMessage(messageId);
    if (!found) return;
    found.m.text = newText;
    found.m.edited = true;
    renderMessages();
});

connection.on("MessageDeleted", (messageId) => {
    const found = findMessage(messageId);
    if (!found) return;
    chats[found.key] = chats[found.key].filter(x => x.id !== messageId);
    renderMessages();
});

connection.onreconnected(async () => {
    if (!currentUsername) return;
    try {
        await connection.invoke("JoinToMessage", currentUsername);
        if (selectedUserId !== null) {
            await openChat({ id: selectedUserId, username: selectedUsername });
        }
    } catch (err) {
        console.error(err.toString());
    }
});

document.getElementById("sendButton").addEventListener("click", () => {
    const input = document.getElementById("messageInput");
    const text = input.value.trim();

    if (selectedUserId === null || !text) return;

    connection.invoke("SendPrivateMessage", selectedUserId, text)
        .catch(err => console.error(err.toString()));

    input.value = "";
});

document.getElementById("messageInput").addEventListener("keydown", (e) => {
    if (e.key === "Enter") document.getElementById("sendButton").click();
});

document.getElementById("joinButton").addEventListener("click", async (e) => {
    e.preventDefault();

    const username = document.getElementById("inputUser").value.trim();
    if (!username) return;

    currentUsername = username;
    await startPromise;

    try {
        await connection.invoke("JoinToMessage", username);
        document.getElementById("join").style.display = "none";
        document.getElementById("users").style.display = "block";
    } catch (err) {
        console.error(err.toString());
    }
});

document.addEventListener("click", () => {
    if (activeMessageId !== null) {
        activeMessageId = null;
        renderMessages();
    }
});