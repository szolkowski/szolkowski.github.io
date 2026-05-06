require 'open3'

module Jekyll
  # Runs `pagefind --site <dest>` after every build so the header search
  # works without a separate `npm run build` step. Mirrors the
  # WebpGenerator pattern: hooks :site, :post_write, shells out to a
  # native binary, no-ops gracefully if the binary isn't present.
  #
  # We invoke the platform-specific native binary directly (e.g.
  # `node_modules/@pagefind/darwin-arm64/bin/pagefind_extended`) instead of
  # the npm wrapper at `node_modules/.bin/pagefind`. The wrapper is a Node
  # script — running it via Jekyll's shellout picks up whatever `node` is
  # first on PATH, which on this machine resolves to the macOS system Node
  # (darwin-x64) and fails to find a darwin-x64 prebuild. Native binary
  # has no such ambiguity.
  Jekyll::Hooks.register :site, :post_write do |site|
    next unless File.directory?(site.dest)

    bin = Dir.glob(File.join(site.source, 'node_modules', '@pagefind', '*', 'bin', '*'))
             .reject { |p| p.end_with?('.sha256') }
             .find { |p| File.executable?(p) && !File.directory?(p) }

    unless bin
      Jekyll.logger.warn 'PagefindRunner:', 'no pagefind binary in node_modules/@pagefind/*/bin — run `npm install`. Header search will be empty.'
      next
    end

    _out, status = Open3.capture2e(bin, '--site', site.dest, '--quiet')
    if status.success?
      Jekyll.logger.info 'PagefindRunner:', "indexed #{site.dest}/pagefind/"
    else
      Jekyll.logger.warn 'PagefindRunner:', "#{File.basename(bin)} exited non-zero (header search will be empty)"
    end
  end
end
