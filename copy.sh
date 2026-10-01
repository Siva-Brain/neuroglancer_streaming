set -x
cd /home/users/azhar/projectM/viewer_data/fused_8um
tar -cf /home/users/siva/projects/hb02_L7.tar \
  hb02_fused.zarr/zarr.json  hb02_fused.zarr/7 \
  hb02_labels.zarr/zarr.json hb02_labels.zarr/7 \
  manifest.json
