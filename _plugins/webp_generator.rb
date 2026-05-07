require 'open3'

module Jekyll
  # Generates .webp siblings for every .png / .jpg / .jpeg in the rendered
  # site under assets/img/. Runs on :site, :post_write so it sees the
  # final destination paths Jekyll just copied — no source-tree pollution,
  # no clash with the existing pngquant'd PNGs.
  #
  # Requires the `cwebp` binary (Google's libwebp encoder):
  #   - macOS:  brew install webp
  #   - Ubuntu: apt-get install -y webp
  #
  # If cwebp isn't on PATH the plugin warns once and skips. WebP siblings are
  # still useful to have on disk — `<picture>` markup that prefers them is a
  # follow-up, kept out of this PR per the SEO/OG validator caveat.
  Jekyll::Hooks.register :site, :post_write do |site|
    img_dir = File.join(site.dest, 'assets', 'img')
    next unless File.directory?(img_dir)

    unless system('which cwebp > /dev/null 2>&1')
      Jekyll.logger.warn 'WebpGenerator:', 'cwebp not found on PATH — skipping WebP generation'
      next
    end

    converted = 0
    skipped = 0
    # FNM_CASEFOLD so we match .PNG/.JPG too without double-counting on
    # case-insensitive filesystems (macOS default APFS).
    Dir.glob(File.join(img_dir, '*.{png,jpg,jpeg}'), File::FNM_CASEFOLD).each do |src|
      webp = src.sub(/\.(png|jpe?g)\z/i, '.webp')
      if File.exist?(webp) && File.mtime(webp) >= File.mtime(src)
        skipped += 1
        next
      end
      _, status = Open3.capture2e('cwebp', '-quiet', '-q', '80', src, '-o', webp)
      if status.success?
        converted += 1
      else
        Jekyll.logger.warn 'WebpGenerator:', "cwebp failed on #{File.basename(src)}"
      end
    end

    Jekyll.logger.info 'WebpGenerator:', "converted #{converted}, skipped #{skipped} (already up to date)"
  end
end
