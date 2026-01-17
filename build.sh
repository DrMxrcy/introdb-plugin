#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_DIR="$SCRIPT_DIR/build"
SRC_DIR="$SCRIPT_DIR/introdb-plugin/IntroDbPlugin"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

print_status() {
    echo -e "${GREEN}[BUILD]${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Clean build directory
clean_build() {
    print_status "Cleaning build directory..."
    rm -rf "$BUILD_DIR"
    mkdir -p "$BUILD_DIR"
}

# Check if .NET 9 SDK is available
check_net9_available() {
    dotnet --list-sdks 2>/dev/null | grep -q "^9\."
}

# Build Jellyfin version
build_jellyfin() {
    local version=$1
    local output_dir="$BUILD_DIR/jellyfin-$version"

    # Jellyfin 10.11+ requires .NET 9
    if [ "$version" = "10.11" ]; then
        if ! check_net9_available; then
            print_warning "Jellyfin 10.11 requires .NET 9 SDK which is not installed."
            print_warning "Skipping Jellyfin 10.11 build. Install .NET 9 SDK to build this version."
            return 0
        fi
    fi

    print_status "Building for Jellyfin $version..."

    dotnet build "$SRC_DIR/IntroDbPlugin.csproj" \
        -c Release \
        -p:JellyfinVersion="$version" \
        -o "$output_dir" \
        --nologo

    # Clean up unnecessary files
    rm -f "$output_dir"/*.deps.json
    rm -f "$output_dir"/*.pdb

    # Copy meta.json and update targetAbi for this version
    cp "$SRC_DIR/meta.json" "$output_dir/"
    sed -i "s/\"targetAbi\": \"10.10.0.0\"/\"targetAbi\": \"$version.0.0\"/" "$output_dir/meta.json"

    print_status "Jellyfin $version build complete: $output_dir"
}

# Build Emby version
build_emby() {
    local output_dir="$BUILD_DIR/emby"

    print_status "Building for Emby..."

    dotnet build "$SRC_DIR/IntroDbPlugin.Emby.csproj" \
        -c Release \
        -o "$output_dir" \
        --nologo

    # Clean up unnecessary files
    rm -f "$output_dir"/*.deps.json
    rm -f "$output_dir"/*.pdb

    print_status "Emby build complete: $output_dir"
}

# Show usage
usage() {
    echo "Usage: $0 [OPTIONS]"
    echo ""
    echo "Options:"
    echo "  --all           Build all versions (default)"
    echo "  --jellyfin-10.10  Build for Jellyfin 10.10 only"
    echo "  --jellyfin-10.11  Build for Jellyfin 10.11 only"
    echo "  --emby          Build for Emby only"
    echo "  --clean         Clean build directory only"
    echo "  --help          Show this help message"
    echo ""
    echo "Examples:"
    echo "  $0              # Build all versions"
    echo "  $0 --jellyfin-10.11  # Build only Jellyfin 10.11"
    echo "  $0 --emby       # Build only Emby"
}

# Main
main() {
    local build_jellyfin_10_10=false
    local build_jellyfin_10_11=false
    local build_emby_flag=false
    local clean_only=false

    # Parse arguments
    if [ $# -eq 0 ]; then
        build_jellyfin_10_10=true
        build_jellyfin_10_11=true
        build_emby_flag=true
    else
        while [ $# -gt 0 ]; do
            case "$1" in
                --all)
                    build_jellyfin_10_10=true
                    build_jellyfin_10_11=true
                    build_emby_flag=true
                    ;;
                --jellyfin-10.10)
                    build_jellyfin_10_10=true
                    ;;
                --jellyfin-10.11)
                    build_jellyfin_10_11=true
                    ;;
                --emby)
                    build_emby_flag=true
                    ;;
                --clean)
                    clean_only=true
                    ;;
                --help|-h)
                    usage
                    exit 0
                    ;;
                *)
                    print_error "Unknown option: $1"
                    usage
                    exit 1
                    ;;
            esac
            shift
        done
    fi

    # Always clean first
    clean_build

    if [ "$clean_only" = true ]; then
        print_status "Clean complete."
        exit 0
    fi

    # Build requested versions
    if [ "$build_jellyfin_10_10" = true ]; then
        build_jellyfin "10.10"
    fi

    if [ "$build_jellyfin_10_11" = true ]; then
        build_jellyfin "10.11"
    fi

    if [ "$build_emby_flag" = true ]; then
        build_emby
    fi

    echo ""
    print_status "Build complete!"
    echo ""
    echo "Build outputs:"
    ls -la "$BUILD_DIR"
}

main "$@"
