/** @type {import('next').NextConfig} */
const nextConfig = {
  // Writes plain HTML to out/ at build time, which nginx serves.
  output: "export",
};

export default nextConfig;
