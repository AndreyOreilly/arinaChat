document.addEventListener("DOMContentLoaded", () => {
  const messagesContainer = document.getElementById("messages");
  const userInput = document.getElementById("user-input");
  const sendButton = document.getElementById("send-button");

  // Добавляем приветственное сообщение
  addMessage(
    "assistant",
    "Привет! Я Арина, и я готова помочь вам. О чём хотите поговорить?"
  );

  // Handle message sending
  async function sendMessage() {
    const message = userInput.value.trim();
    if (!message) return;

    try {
      // Disable input and button while processing
      userInput.disabled = true;
      sendButton.disabled = true;

      // Clear input
      userInput.value = "";

      // Add user message to chat
      addMessage("user", message);

      // Show typing indicator
      const typingIndicator = addMessage(
        "assistant",
        "...",
        "typing-indicator"
      );

      const response = await fetch("/api/chat", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ message }),
      });

      const data = await response.json();

      // Remove typing indicator
      typingIndicator.remove();

      if (!response.ok) {
        throw new Error(data.message || "Ошибка при получении ответа");
      }

      if (data.choices && data.choices[0] && data.choices[0].message) {
        const assistantMessage = data.choices[0].message.content;
        addMessage("assistant", assistantMessage);
      } else {
        throw new Error("Неверный формат ответа от сервера");
      }
    } catch (error) {
      console.error("Error:", error);
      addMessage("assistant", `Извините, произошла ошибка: ${error.message}`);
    } finally {
      // Re-enable input and button
      userInput.disabled = false;
      sendButton.disabled = false;
      userInput.focus();
    }
  }

  // Add message to chat
  function addMessage(role, content, extraClass = "") {
    const messageDiv = document.createElement("div");
    messageDiv.className = `message ${role} ${extraClass}`;
    messageDiv.textContent = content;
    messagesContainer.appendChild(messageDiv);
    messagesContainer.scrollTop = messagesContainer.scrollHeight;
    return messageDiv;
  }

  // Event listeners
  sendButton.addEventListener("click", sendMessage);

  userInput.addEventListener("keypress", (e) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      sendMessage();
    }
  });

  // Auto-resize textarea
  userInput.addEventListener("input", () => {
    userInput.style.height = "auto";
    userInput.style.height = Math.min(userInput.scrollHeight, 150) + "px";
  });
});
