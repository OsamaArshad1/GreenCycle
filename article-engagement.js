(function () {
  const engagement = document.querySelector('.article-engagement');
  if (!engagement) return;

  const articleId = engagement.dataset.articleId || window.location.pathname;
  const commentsArticleId = engagement.dataset.commentsId || articleId;
  const viewsArticleId = engagement.dataset.viewsId || commentsArticleId;
  const baseReaders = Number.parseInt(engagement.dataset.readerBase || '0', 10);
  const readerCount = engagement.querySelector('[data-reader-count]');
  const shareButton = engagement.querySelector('[data-share-button]');
  const shareStatus = engagement.querySelector('[data-share-status]');
  const isArabic = document.documentElement.lang === 'ar';
  const commentForm = document.querySelector('[data-article-comment-form]');
  const commentsSection = document.querySelector('.article-comments');

  function apiUrl(path) {
    const isBackendHost = window.location.port === '5294' || window.location.port === '5000';
    const isLocalPage = window.location.protocol === 'file:' ||
      window.location.hostname === 'localhost' ||
      window.location.hostname === '127.0.0.1';
    const configuredApiOrigin = window.GreenCycleConfig?.apiBaseUrl || window.GREEN_CYCLE_API_ORIGIN || '';
    const apiOrigin = isBackendHost ? '' : (isLocalPage ? 'http://localhost:5294' : configuredApiOrigin);
    return `${apiOrigin}${path}`;
  }

  async function requestJson(path, options) {
    const url = apiUrl(path);
    let response;
    try {
      response = await fetch(url, {
        headers: { 'Content-Type': 'application/json' },
        ...options
      });
    } catch (error) {
      throw new Error(isArabic
        ? `تعذر الاتصال بالخادم: ${url}`
        : `Could not connect to the server: ${url}`);
    }
    const data = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new Error(data.message || data.Message || (isArabic ? 'تعذر إكمال الطلب.' : 'The request could not be completed.'));
    }
    return data;
  }

  function getVisitorKey() {
    try {
      const storageKey = 'greenCycleArticleVisitor';
      let visitorKey = localStorage.getItem(storageKey);
      if (!visitorKey) {
        visitorKey = crypto.randomUUID
          ? crypto.randomUUID()
          : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
        localStorage.setItem(storageKey, visitorKey);
      }
      return visitorKey;
    } catch (error) {
      return '';
    }
  }

  if (readerCount) {
    readerCount.textContent = baseReaders.toLocaleString(document.documentElement.lang || 'en');
  }

  async function recordArticleView() {
    if (!readerCount) return;

    try {
      const result = await requestJson(`/api/articles/${encodeURIComponent(viewsArticleId)}/views`, {
        method: 'POST',
        body: JSON.stringify({
          visitorKey: getVisitorKey(),
          readerBase: baseReaders
        })
      });
      const views = Number.isFinite(result.views) ? result.views : baseReaders;
      readerCount.textContent = views.toLocaleString(document.documentElement.lang || 'en');
    } catch (error) {
      readerCount.textContent = baseReaders.toLocaleString(document.documentElement.lang || 'en');
    }
  }

  recordArticleView();

  if (shareButton) {
    const shareText = engagement.dataset.shareText || document.title;
    const shareUrl = window.location.href;

    const icons = {
      share: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M18 8a3 3 0 1 0-2.83-4H15a3 3 0 0 0 .1 2.4l-6.02 3.51a3 3 0 1 0 0 4.18l6.02 3.51A3 3 0 1 0 15.83 15l-6.02-3.51a3 3 0 0 0 0-.98L15.83 7A3 3 0 0 0 18 8Z"/></svg>',
      whatsapp: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M17.47 14.38c-.3-.15-1.76-.87-2.03-.97-.27-.1-.47-.15-.67.15-.2.3-.77.97-.94 1.17-.17.2-.35.22-.65.07-.3-.15-1.25-.46-2.38-1.47-.88-.78-1.47-1.75-1.65-2.05-.17-.3-.02-.46.13-.6.13-.13.3-.35.45-.52.15-.17.2-.3.3-.5.1-.2.05-.37-.02-.52-.07-.15-.67-1.6-.91-2.2-.24-.58-.49-.5-.67-.5-.17 0-.37-.02-.57-.02s-.52.07-.8.37c-.27.3-1.05 1.02-1.05 2.5s1.07 2.9 1.22 3.1c.15.2 2.1 3.2 5.08 4.49.71.3 1.26.49 1.7.62.71.23 1.36.2 1.87.12.57-.08 1.76-.72 2.01-1.42.25-.7.25-1.3.17-1.42-.07-.13-.27-.2-.57-.35Z"/><path d="M12.02 2C6.5 2 2.02 6.48 2.02 12c0 1.85.5 3.58 1.36 5.07L2 22l5.06-1.33A9.94 9.94 0 0 0 12.02 22C17.53 22 22 17.52 22 12S17.53 2 12.02 2Zm0 18.13c-1.66 0-3.2-.48-4.5-1.31l-.32-.2-3 .79.8-2.93-.21-.3a8.11 8.11 0 0 1-1.27-4.33c0-4.49 3.66-8.14 8.15-8.14 4.48 0 8.13 3.65 8.13 8.14 0 4.5-3.65 8.28-8.13 8.28Z"/></svg>',
      facebook: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M13.5 21v-7.5h2.5l.5-3h-3V8.5c0-.87.24-1.46 1.5-1.46H16.5V4.35c-.27-.04-1.2-.11-2.27-.11-2.25 0-3.79 1.37-3.79 3.89V10.5H8v3h2.44V21h3.06Z"/></svg>',
      x: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M18.9 3H21l-6.53 7.47L22.5 21h-6.9l-4.32-5.67L6.3 21H3l7-8L2.9 3h7l3.9 5.2L18.9 3Zm-1.2 16.2h1.15L7.8 4.7H6.55l11.15 14.5Z"/></svg>',
      email: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Zm0 2v.01L12 12l8-5.99V6H4Zm16 12V8.24l-8 5.99-8-5.99V18h16Z"/></svg>',
      copy: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M16 1H4a2 2 0 0 0-2 2v14h2V3h12V1Zm3 4H8a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2Zm0 16H8V7h11v14Z"/></svg>'
    };

    shareButton.innerHTML = `<span class="article-action-icon">${icons.share}</span><span>${shareButton.textContent}</span>`;

    const wrap = document.createElement('span');
    wrap.className = 'article-share-wrap';
    shareButton.insertAdjacentElement('beforebegin', wrap);
    wrap.appendChild(shareButton);

    shareButton.setAttribute('aria-haspopup', 'true');
    shareButton.setAttribute('aria-expanded', 'false');

    const menu = document.createElement('div');
    menu.className = 'article-share-menu';
    menu.setAttribute('role', 'menu');
    menu.hidden = true;

    const labels = isArabic
      ? { whatsapp: 'واتساب', facebook: 'فيسبوك', x: 'إكس (تويتر)', email: 'البريد الإلكتروني', copy: 'نسخ الرابط' }
      : { whatsapp: 'WhatsApp', facebook: 'Facebook', x: 'X (Twitter)', email: 'Email', copy: 'Copy link' };

    const links = [
      { key: 'whatsapp', href: `https://wa.me/?text=${encodeURIComponent(`${shareText} ${shareUrl}`)}` },
      { key: 'facebook', href: `https://www.facebook.com/sharer/sharer.php?u=${encodeURIComponent(shareUrl)}` },
      { key: 'x', href: `https://twitter.com/intent/tweet?url=${encodeURIComponent(shareUrl)}&text=${encodeURIComponent(shareText)}` },
      { key: 'email', href: `mailto:?subject=${encodeURIComponent(shareText)}&body=${encodeURIComponent(shareUrl)}` }
    ];

    links.forEach(({ key, href }) => {
      const link = document.createElement('a');
      link.href = href;
      link.innerHTML = `<span class="article-share-icon">${icons[key]}</span><span>${labels[key]}</span>`;
      link.setAttribute('role', 'menuitem');
      link.target = '_blank';
      link.rel = 'noopener';
      link.addEventListener('click', closeMenu);
      menu.appendChild(link);
    });

    const copyButton = document.createElement('button');
    copyButton.type = 'button';
    copyButton.innerHTML = `<span class="article-share-icon">${icons.copy}</span><span>${labels.copy}</span>`;
    copyButton.setAttribute('role', 'menuitem');
    copyButton.addEventListener('click', async () => {
      try {
        if (navigator.clipboard) {
          await navigator.clipboard.writeText(shareUrl);
        }
        if (shareStatus) {
          shareStatus.textContent = engagement.dataset.shareSuccess || 'Link copied.';
        }
      } catch (error) {
        if (shareStatus) {
          shareStatus.textContent = engagement.dataset.shareError || 'Sharing is unavailable.';
        }
      }
      closeMenu();
    });
    menu.appendChild(copyButton);

    wrap.appendChild(menu);

    function openMenu() {
      menu.hidden = false;
      shareButton.setAttribute('aria-expanded', 'true');
      document.addEventListener('click', handleOutsideClick);
      document.addEventListener('keydown', handleEscape);
    }

    function closeMenu() {
      menu.hidden = true;
      shareButton.setAttribute('aria-expanded', 'false');
      document.removeEventListener('click', handleOutsideClick);
      document.removeEventListener('keydown', handleEscape);
    }

    function handleOutsideClick(event) {
      if (!wrap.contains(event.target)) {
        closeMenu();
      }
    }

    function handleEscape(event) {
      if (event.key === 'Escape') {
        closeMenu();
        shareButton.focus();
      }
    }

    shareButton.addEventListener('click', () => {
      if (menu.hidden) {
        openMenu();
      } else {
        closeMenu();
      }
    });
  }

  if (!commentForm || !commentsSection) return;

  const commentsList = document.createElement('div');
  commentsList.className = 'article-approved-comments';
  commentsList.setAttribute('aria-live', 'polite');

  const commentsListTitle = document.createElement('h3');
  commentsListTitle.className = 'article-approved-comments-title';
  commentsListTitle.textContent = isArabic ? 'التعليقات' : 'Comments';

  const commentStatus = document.createElement('p');
  commentStatus.className = 'article-comment-status';
  commentStatus.setAttribute('role', 'status');

  commentForm.insertAdjacentElement('afterend', commentStatus);
  commentStatus.insertAdjacentElement('afterend', commentsListTitle);
  commentsListTitle.insertAdjacentElement('afterend', commentsList);

  function renderComments(comments) {
    if (!Array.isArray(comments) || comments.length === 0) {
      commentsList.innerHTML = `<p class="article-no-comments">${isArabic ? 'لا توجد تعليقات منشورة حتى الآن.' : 'No approved comments yet.'}</p>`;
      return;
    }

    commentsList.innerHTML = comments.map(comment => {
      const name = escapeHtml(comment.reviewerName || (isArabic ? 'قارئ' : 'Reader'));
      const text = escapeHtml(comment.comment || '');
      const date = comment.createdAtUtc
        ? new Date(comment.createdAtUtc).toLocaleDateString(isArabic ? 'ar' : 'en', { year: 'numeric', month: 'short', day: 'numeric' })
        : '';
      return `
        <article class="article-approved-comment">
          <strong>${name}</strong>
          <time>${escapeHtml(date)}</time>
          <p>${text}</p>
        </article>
      `;
    }).join('');
  }

  function escapeHtml(value) {
    return String(value)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#039;');
  }

  async function loadComments() {
    try {
      const result = await requestJson(`/api/articles/${encodeURIComponent(commentsArticleId)}/comments`);
      renderComments(result.comments || []);
    } catch (error) {
      commentsList.innerHTML = `<p class="article-no-comments">${isArabic ? 'تعذر تحميل التعليقات حالياً.' : 'Comments could not be loaded right now.'}</p>`;
    }
  }

  commentForm.addEventListener('submit', async event => {
    event.preventDefault();

    const nameInput = commentForm.querySelector('[name="name"]');
    const commentInput = commentForm.querySelector('[name="comment"]');
    const reviewerName = nameInput?.value.trim();
    const comment = commentInput?.value.trim();

    if (!reviewerName || !comment) {
      commentStatus.textContent = isArabic ? 'يرجى إدخال الاسم والتعليق.' : 'Please enter your name and comment.';
      return;
    }

    const submitButton = commentForm.querySelector('button[type="submit"]');
    submitButton?.setAttribute('disabled', 'disabled');
    commentStatus.textContent = isArabic ? 'جاري إرسال التعليق للموافقة...' : 'Sending comment for approval...';

    try {
      const result = await requestJson(`/api/articles/${encodeURIComponent(commentsArticleId)}/comments`, {
        method: 'POST',
        body: JSON.stringify({
          reviewerName,
          comment,
          articleTitle: engagement.dataset.shareText || document.title
        })
      });
      commentForm.reset();
      commentStatus.textContent = result.message || (isArabic
        ? 'تم إرسال التعليق وهو بانتظار الموافقة.'
        : 'Comment submitted and waiting for approval.');
      await loadComments();
    } catch (error) {
      commentStatus.textContent = error.message;
    } finally {
      submitButton?.removeAttribute('disabled');
    }
  });

  loadComments();
})();
