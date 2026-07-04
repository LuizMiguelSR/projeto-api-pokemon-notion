function setupJobPage(options) {
  const title = document.getElementById("title");
  const message = document.getElementById("message");
  const progress = document.getElementById("progress");
  const progressBar = document.getElementById("progressBar");
  const progressLabel = document.getElementById("progressLabel");
  const start = document.getElementById("start");
  const result = document.getElementById("result");
  const pokeball = document.querySelector(".pokeball");

  async function requestJson(url, init) {
    const response = await fetch(url, {
      ...init,
      headers: {
        Accept: "application/json",
        ...(init && init.headers ? init.headers : {})
      }
    });

    if (response.redirected && response.url.includes("/auth/denied")) {
      throw new Error("Você está logado, mas este e-mail não está autorizado. Saia e entre com o e-mail permitido.");
    }

    if (response.redirected || response.status === 401) {
      throw new Error("Você não está logado com um e-mail permitido. Acesse /auth/login e tente novamente.");
    }

    if (response.status === 403) {
      throw new Error("Você está logado, mas este e-mail não está autorizado. Saia e entre com o e-mail permitido.");
    }

    if (!response.ok) {
      const text = await response.text();
      throw new Error(text || `HTTP ${response.status}`);
    }

    return await response.json();
  }

  function updateProgress(job) {
    const percent = Number.isFinite(job.percent) ? job.percent : 0;
    progressBar.style.width = `${Math.max(0, Math.min(100, percent))}%`;
    progressLabel.textContent = job.total
      ? `${percent}% - ${job.processed || 0} de ${job.total}`
      : `${percent}%`;
    if (job.message) {
      message.textContent = job.message;
    }
  }

  async function poll(jobId) {
    const job = await requestJson(`/api/jobs/${jobId}`);
    if (!job) return;
    updateProgress(job);

    if (job.state === "running") {
      if (!job.message) {
        message.textContent = `Job ${job.id} em execução.`;
      }
      setTimeout(() => poll(jobId).catch(showError), 1800);
      return;
    }

    progressBar.style.width = "100%";
    progressLabel.textContent = "100%";
    result.hidden = false;
    result.textContent = JSON.stringify(job.result || job.error || job, null, 2);

    if (job.state === "completed") {
      title.textContent = options.doneTitle;
      title.className = "ok";
      message.textContent = "Processamento finalizado com sucesso.";
      pokeball?.classList.add("caught");
      start.disabled = false;
      return;
    }

    title.textContent = options.errorTitle;
    title.className = "error";
    message.textContent = "O job terminou com erro.";
    pokeball?.classList.add("caught");
    start.disabled = false;
  }

  function showError(error) {
    progress.hidden = true;
    progressLabel.hidden = true;
    result.hidden = false;
    title.textContent = options.errorTitle;
    title.className = "error";
    message.textContent = "Não foi possível executar a operação.";
    result.textContent = error.message;
    pokeball?.classList.add("caught");
    start.disabled = false;
  }

  start.addEventListener("click", async () => {
    start.disabled = true;
    progress.hidden = false;
    progressLabel.hidden = false;
    progressBar.style.width = "0%";
    progressLabel.textContent = "0%";
    result.hidden = true;
    title.textContent = options.title;
    title.className = "";
    message.textContent = "Criando job em background.";
    pokeball?.classList.remove("caught");

    try {
      const job = await requestJson(options.startEndpoint, { method: "POST" });
      if (!job) return;
      await poll(job.id);
    } catch (error) {
      showError(error);
    }
  });
}
