document.addEventListener("DOMContentLoaded", function () {
    if (typeof hljs !== "undefined") {
        hljs.highlightAll();
    }

    document.querySelectorAll(".open-code-modal").forEach(function (button) {
        button.addEventListener("click", function () {
            var targetId = button.getAttribute("data-target");
            var modal = document.getElementById(targetId);
            if (modal) {
                modal.style.display = "block";
                if (typeof hljs !== "undefined") {
                    modal.querySelectorAll("pre code").forEach(function (block) {
                        hljs.highlightElement(block);
                    });
                }
            } else {
                console.warn("Modal not found for target:", targetId);
            }
        });
    });

    document.querySelectorAll(".code-modal-close").forEach(function (closeBtn) {
        closeBtn.addEventListener("click", function () {
            closeBtn.closest(".code-modal").style.display = "none";
        });
    });

    window.addEventListener("click", function (event) {
        document.querySelectorAll(".code-modal").forEach(function (modal) {
            if (event.target === modal) {
                modal.style.display = "none";
            }
        });
    });

    document.addEventListener("keydown", function (event) {
        if (event.key === "Escape") {
            document.querySelectorAll(".code-modal").forEach(function (modal) {
                if (modal.style.display === "block") {
                    modal.style.display = "none";
                }
            });
        }
    });
});

document.addEventListener("click", function (event) {
    var button = event.target.closest(".copy-btn");
    if (!button) return;
    var code = button.nextElementSibling.querySelector("code");
    if (!code) return;
    var text = code.innerText;

    navigator.clipboard.writeText(text).then(function () {
        button.classList.add("copied");
        button.innerHTML = "<i class='fas fa-check'></i> Copied!";
        setTimeout(function () {
            button.classList.remove("copied");
            button.innerHTML = "<i class='fas fa-copy'></i> Copy";
        }, 1500);
    }).catch(function () {
        button.innerHTML = "<i class='fas fa-times'></i> Failed";
        setTimeout(function () {
            button.innerHTML = "<i class='fas fa-copy'></i> Copy";
        }, 1500);
    });
});
