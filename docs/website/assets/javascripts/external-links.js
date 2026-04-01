function updateExternalLinks(scope) {
  const root = scope instanceof Document ? scope : document;
  const links = root.querySelectorAll("a[href]");

  for (const link of links) {
    const href = link.getAttribute("href");
    if (!href) {
      continue;
    }

    if (href.startsWith("#") || href.startsWith("mailto:") || href.startsWith("tel:")) {
      continue;
    }

    let url;
    try {
      url = new URL(href, window.location.origin);
    } catch {
      continue;
    }

    if (url.origin === window.location.origin) {
      continue;
    }

    link.setAttribute("target", "_blank");
    link.setAttribute("rel", "noopener noreferrer");
  }
}

if (typeof document$ !== "undefined") {
  document$.subscribe((document) => {
    updateExternalLinks(document);
  });
} else {
  updateExternalLinks(document);
}
